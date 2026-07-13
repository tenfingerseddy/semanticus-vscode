import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onSpecChange, pickSpecFile } from './bridge';
import { DaxField } from './daxeditor';
import { EMPTY_MODEL, type DaxModel } from './dax';
import { FORMAT_PRESETS } from './advmodels';

// Wire shapes mirror Semanticus.Engine/Spec.cs (camelCased on the wire).
interface SpecColumn { name: string; dataType?: string; sourceColumn?: string; isKey?: boolean; hidden?: boolean; summarizeBy?: string; }
interface SpecTable { name: string; role?: string; entity?: string; schema?: string; mExpression?: string; calculatedExpression?: string; sourceName?: string; columns: SpecColumn[]; }
interface SpecRelationship { fromTable: string; fromColumn: string; toTable: string; toColumn: string; cardinality?: string; crossFilter?: string; isActive?: boolean; }
interface SpecMeasure { table: string; name: string; dax?: string; formatString?: string; displayFolder?: string; }
interface SpecSource { kind?: string; server?: string; database?: string; schema?: string; }
interface SpecDateTable { name?: string; startExpr?: string; endExpr?: string; markAsDate?: boolean; }
interface ModelSpec {
  name?: string; compatibilityLevel?: number; storageMode?: string; source?: SpecSource;
  tables: SpecTable[]; relationships: SpecRelationship[]; measures: SpecMeasure[];
  timeIntelligence: string[]; timeIntelligenceBaseMeasures?: string[]; dateTable?: SpecDateTable;
}
interface SpecSnapshot { version: number; source: string; spec: ModelSpec | null; }
interface SpecBuildReport { revision: number; created: string[]; skipped: string[]; errors: string[]; tablesBefore: number; tablesAfter: number; measuresBefore: number; measuresAfter: number; note?: string; }

const ROLE_COLOR: Record<string, string> = { fact: 'var(--sem-accent)', dimension: '#3FB0E6', date: 'var(--sem-warn)', calculated: '#17B3A3', isolated: 'var(--sem-muted)' };
const SOURCE_LABEL: Record<string, string> = { manual: 'edited', 'autogenerate-model': 'from open model', 'autogenerate-fabric': 'from SQL', file: 'loaded from file' };

// The set of star-schema roles a table can carry (mirrors SpecTable.Role in Spec.cs). "Measure group" is not a role —
// it is a convenience choice on + Table that creates a local calculated table ({ BLANK() }) to park measures on. (A
// plain column-less import table would build with an unresolvable default M partition, so it would not materialise.)
const TABLE_ROLES = ['fact', 'dimension', 'date', 'calculated', 'isolated'];
// Mirrors Semanticus.Engine LocalEngine.SettableColumnDataTypes — the exact set set_column_data_type accepts. The
// Properties grid reads this list from get_properties at runtime; a not-yet-built spec column has no live descriptor
// to read, so the same allow-list is mirrored here as the single documented source (keep in sync with the engine).
const SPEC_DATA_TYPES = ['String', 'Int64', 'Decimal', 'Double', 'DateTime', 'Boolean'];
const SUMMARIZE_BY = ['None', 'Sum', 'Average', 'Count', 'DistinctCount', 'Min', 'Max'];
const CROSSFILTER = [{ value: 'OneDirection', label: 'Single' }, { value: 'BothDirections', label: 'Both' }];
// Relationship cardinality (reads From→To; mirrors SpecRelationship.Cardinality in Spec.cs). Many-to-one is the default —
// an unset value builds many→one, so leaving it alone preserves the classic fact→dimension shape.
const CARDINALITY = [
  { value: 'manyToOne', label: 'Many-to-one' },
  { value: 'oneToOne', label: 'One-to-one' },
  { value: 'oneToMany', label: 'One-to-many' },
  { value: 'manyToMany', label: 'Many-to-many' },
];
// Resolve a stored cardinality to a real dropdown option: null/undefined/empty/whitespace all mean the many-to-one
// default (mirrors the engine, where an empty Cardinality parses as manyToOne), so the Sel never renders a blank/stray.
const normCardinality = (c?: string): string => { const v = (c ?? '').trim(); return v === '' ? 'manyToOne' : v; };

const clone = <T,>(x: T): T => JSON.parse(JSON.stringify(x)) as T;
function uniqueName(existing: string[], base: string): string {
  const set = new Set(existing.map((s) => s.toLowerCase()));
  if (!set.has(base.toLowerCase())) return base;
  let i = 2; while (set.has(`${base} ${i}`.toLowerCase())) i++;
  return `${base} ${i}`;
}

// The Spec tab: the model spec Claude and the human iterate, auto-generated first then refined, then built. The
// structured view is INLINE-EDITABLE (tables, columns + types, measures + DAX, relationships) with "Edit JSON" kept
// as an advanced escape hatch. Edits mutate a draft ModelSpec; Save serialises it through setSpec — the SAME op the
// MCP door drives, so dual-drive + the live spec/didChange broadcast are preserved (no new write path).
export function SpecView({ session }: { session?: { modelName?: string; tables?: number; measures?: number } | null }) {
  const [snap, setSnap] = useState<SpecSnapshot | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [report, setReport] = useState<SpecBuildReport | null>(null);
  const [editingJson, setEditingJson] = useState(false);
  const [jsonDraft, setJsonDraft] = useState('');
  const [draft, setDraft] = useState<ModelSpec | null>(null);   // the editable working copy
  const [dirty, setDirty] = useState(false);
  const [baseVersion, setBaseVersion] = useState<number | null>(null);   // the snap version the draft was synced from
  const [showFabric, setShowFabric] = useState(false);
  const [fabric, setFabric] = useState({ server: '', database: '', storageMode: 'import', authMode: 'azcli', tenant: '' });
  const reloadTimer = useRef<number | undefined>(undefined);

  async function load() {
    try { setSnap(await rpc<SpecSnapshot>('getSpec')); setErr(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  useEffect(() => {
    void load();
    const off = onSpecChange(() => { window.clearTimeout(reloadTimer.current); reloadTimer.current = window.setTimeout(() => void load(), 250); });
    return () => { off(); window.clearTimeout(reloadTimer.current); };
  }, []);

  // Sync the editable draft from the server whenever there are NO unsaved edits — so an MCP-driven change lands live.
  // While dirty, the draft is kept intact (never silently lose edits); a server change is surfaced as a reload notice.
  useEffect(() => {
    if (!dirty) { setDraft(snap?.spec ? clone(snap.spec) : null); setBaseVersion(snap?.version ?? null); }
  }, [snap, dirty]);

  const spec = draft;
  const serverChanged = dirty && snap != null && baseVersion != null && snap.version !== baseVersion;
  const counts = useMemo(() => spec && ({
    tables: spec.tables?.length ?? 0,
    relationships: spec.relationships?.length ?? 0,
    measures: spec.measures?.length ?? 0,
    facts: spec.tables?.filter((t) => t.role === 'fact').length ?? 0,
    dims: spec.tables?.filter((t) => t.role === 'dimension').length ?? 0,
  }), [spec]);

  // The one mutator every inline control funnels through: clone the draft, apply the change, mark dirty.
  const edit = (mut: (s: ModelSpec) => void) => {
    setDraft((prev) => { if (!prev) return prev; const next = clone(prev); mut(next); return next; });
    setDirty(true);
  };

  async function run(label: string, fn: () => Promise<unknown>) {
    setBusy(label); setErr(null);
    try { await fn(); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }
  const autogenModel = () => run('model', async () => { setReport(null); setDirty(false); setSnap(await rpc<SpecSnapshot>('autogenerateSpecFromModel', 'human')); });
  const autogenFabric = () => run('fabric', async () => {
    if (!fabric.server.trim() || !fabric.database.trim()) { setErr('SQL endpoint and database are required.'); return; }
    setReport(null);
    // Positional RPC (byPosition): tenantId rides LAST and is OMITTED when blank, so the common (no-tenant) case still
    // binds against an older engine build that predates the 6th param — a real skew when the bundle is ahead of the
    // engine in F5/dev. When a tenant IS set against an old engine, the arg-count mismatch failing is acceptable.
    const args: unknown[] = [fabric.server.trim(), fabric.database.trim(), fabric.authMode, fabric.storageMode, 'human'];
    const tenant = fabric.tenant.trim();
    if (tenant) args.push(tenant);
    setSnap(await rpc<SpecSnapshot>('autogenerateSpecFromFabric', ...args));
    setShowFabric(false);
  });
  const build = () => run('build', async () => setReport(await rpc<SpecBuildReport>('buildModelFromSpec', 'human')));
  const clear = () => run('clear', async () => { setReport(null); setDirty(false); setSnap(await rpc<SpecSnapshot>('clearSpec', 'human')); });
  const save = () => run('save', async () => {
    if (!draft) return;
    const res = await rpc<SpecSnapshot>('setSpec', JSON.stringify(draft), 'human');
    setDirty(false); setSnap(res);
  });
  const loadFile = async () => {
    const path = await pickSpecFile('open');
    if (!path) return;
    await run('load', async () => { setReport(null); setDirty(false); setSnap(await rpc<SpecSnapshot>('loadSpec', path, 'human')); });
  };
  const saveFile = async () => {
    if (!snap?.spec || dirty) return;
    const suggested = `${(snap.spec.name || session?.modelName || 'model').replace(/[^\w.-]+/g, '_')}.spec.json`;
    const path = await pickSpecFile('save', suggested);
    if (!path) return;
    await run('save-file', async () => { setSnap(await rpc<SpecSnapshot>('saveSpec', path)); });
  };
  const discard = () => { setDirty(false); };   // the sync effect reloads the draft from the server snapshot
  const openJson = () => { setJsonDraft(JSON.stringify(draft ?? {}, null, 2)); setEditingJson(true); };
  const applyJson = () => run('apply', async () => {
    let parsed: ModelSpec;
    try { parsed = JSON.parse(jsonDraft) as ModelSpec; }
    catch (e) { throw new Error('Invalid JSON: ' + (e as Error).message); }
    const res = await rpc<SpecSnapshot>('setSpec', JSON.stringify(parsed), 'human');
    setDirty(false); setSnap(res); setEditingJson(false);
  });
  const startBlank = () => run('blank', async () => {
    const blank: ModelSpec = { name: session?.modelName || 'New model', compatibilityLevel: 1604, storageMode: 'import', tables: [], relationships: [], measures: [], timeIntelligence: [] };
    setReport(null); setDirty(false); setSnap(await rpc<SpecSnapshot>('setSpec', JSON.stringify(blank), 'human'));
  });

  return (
    <div className="h-full flex flex-col" style={{ color: 'var(--sem-fg)' }}>
      {/* Toolbar */}
      <div className="flex items-center gap-2 px-4 py-2 border-b flex-wrap text-[12px]" style={{ borderColor: 'var(--sem-border)' }}>
        <div>
          <div className="font-semibold">Model Spec</div>
          <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>One shared draft for you and AI Assistant. Review it here before anything is built.</div>
        </div>
        {snap?.spec && <Chip>{SOURCE_LABEL[snap.source] ?? snap.source} · v{snap.version}</Chip>}
        {dirty && <span className="text-[11px] font-medium" style={{ color: 'var(--sem-warn)' }} title="You have unsaved changes to the spec">● Unsaved changes</span>}
        <div className="flex-1" />
        {dirty && <Btn primary onClick={save} busy={busy === 'save'}>Save</Btn>}
        {dirty && <Btn onClick={discard}>Discard</Btn>}
        {spec && <>
          <Btn onClick={() => void loadFile()} busy={busy === 'load'}>Open spec…</Btn>
          <Btn onClick={() => void saveFile()} busy={busy === 'save-file'} disabled={dirty} title={dirty ? 'Save draft changes first' : 'Save this Model Spec as a JSON file'}>Save spec…</Btn>
          <Btn onClick={autogenModel} busy={busy === 'model'}>Autogenerate from model</Btn>
          <Btn onClick={() => setShowFabric((v) => !v)}>Autogenerate from SQL…</Btn>
          <Btn onClick={() => { if (editingJson) setEditingJson(false); else openJson(); }}>{editingJson ? 'Hide JSON' : 'Edit JSON'}</Btn>
          <Btn onClick={clear} busy={busy === 'clear'}>Clear</Btn>
          <Btn primary onClick={build} busy={busy === 'build'} disabled={!spec.tables?.length || dirty}
            title={dirty ? 'Save your spec changes first' : 'Adds the reviewed objects in one undoable step. It does not publish.'}>Build into model →</Btn>
        </>}
      </div>

      {showFabric && (
        <div className="flex flex-col gap-2 px-4 py-2 border-b text-[12px]" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
          <div className="flex items-end gap-2 flex-wrap">
            <Field label="SQL endpoint" value={fabric.server} onChange={(v) => setFabric((f) => ({ ...f, server: v }))} placeholder="server.example.com" wide />
            <Field label="Database" value={fabric.database} onChange={(v) => setFabric((f) => ({ ...f, database: v }))} placeholder="Source database" />
            <label className="flex flex-col gap-1">
              <span style={{ color: 'var(--sem-muted)' }}>Storage</span>
              <select value={fabric.storageMode} onChange={(e) => setFabric((f) => ({ ...f, storageMode: e.target.value }))}
                style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '4px 8px' }}>
                <option value="import">Import</option>
                <option value="directLake">Direct Lake</option>
              </select>
            </label>
            <label className="flex flex-col gap-1">
              <span style={{ color: 'var(--sem-muted)' }}>Sign-in</span>
              <select value={fabric.authMode} onChange={(e) => setFabric((f) => ({ ...f, authMode: e.target.value }))}
                style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '4px 8px' }}>
                <option value="azcli">Azure CLI</option>
                <option value="interactive">Interactive sign-in</option>
                <option value="devicecode">Device code</option>
              </select>
            </label>
            <Field label="Tenant (optional)" value={fabric.tenant} onChange={(v) => setFabric((f) => ({ ...f, tenant: v }))} placeholder="Tenant id or domain, e.g. contoso.com (blank = your az default)" wide />
            <Btn primary onClick={autogenFabric} busy={busy === 'fabric'}>Introspect →</Btn>
          </div>
          <span style={{ color: 'var(--sem-muted)' }}>Reads table and column metadata only. Nothing is copied into the model until you review the draft and choose Build into model.</span>
        </div>
      )}

      {err && <Banner tone="bad">{err}</Banner>}
      {serverChanged && (
        <div className="mx-4 my-2 rounded px-3 py-2 text-[12px] flex items-center gap-3" style={{ background: 'color-mix(in srgb,var(--sem-warn) 14%, transparent)', color: 'var(--sem-warn)' }}>
          <span>The spec changed elsewhere (now v{snap?.version}). Your unsaved edits are kept.</span>
          <button onClick={discard} className="underline" style={{ color: 'var(--sem-warn)' }}>Load the server version</button>
        </div>
      )}
      {report && <BuildReport report={report} onClose={() => setReport(null)} />}

      <div className="flex-1 min-h-0 overflow-auto">
        {editingJson ? (
          <div className="p-4 flex flex-col gap-2 h-full">
            <textarea value={jsonDraft} onChange={(e) => setJsonDraft(e.target.value)} spellCheck={false}
              className="flex-1 min-h-[300px] font-mono text-[12px] p-3 rounded"
              style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', resize: 'vertical' }} />
            <div className="flex items-center gap-2">
              <Btn primary onClick={applyJson} busy={busy === 'apply'}>Apply JSON</Btn>
              <Btn onClick={() => setEditingJson(false)}>Cancel</Btn>
              <span style={{ color: 'var(--sem-muted)' }} className="text-[11px]">Edit the spec as JSON, or let your AI Assistant refine it through the shared connection; both update here live.</span>
            </div>
          </div>
        ) : !spec ? (
          <SpecWizard session={session} busy={busy} onAutogen={autogenModel} onSql={() => setShowFabric(true)} onBlank={startBlank} onLoad={() => void loadFile()} />
        ) : (
          <SpecEditor spec={spec} counts={counts} edit={edit} />
        )}
      </div>
    </div>
  );
}

// ---- the inline-editable structured view -----------------------------------------------------------
function SpecEditor({ spec, counts, edit }: { spec: ModelSpec; counts: ReturnType<() => { tables: number; relationships: number; measures: number; facts: number; dims: number }> | null; edit: (mut: (s: ModelSpec) => void) => void }) {
  // A DAX completion model built from the SPEC's OWN tables/columns/measures (not the live model) so IntelliSense
  // works before the model is built — the symbols only exist in this draft spec.
  const daxModel: DaxModel = useMemo(() => spec ? ({
    tables: (spec.tables ?? []).map((t) => t.name),
    measures: (spec.measures ?? []).map((m) => ({ name: m.name, table: m.table })),
    columns: (spec.tables ?? []).flatMap((t) => (t.columns ?? []).map((c) => ({ table: t.name, name: c.name }))),
    functions: [],
  }) : EMPTY_MODEL, [spec]);

  const tableNames = (spec.tables ?? []).map((t) => t.name);
  const folderSuggestions = useMemo(() => Array.from(new Set((spec.measures ?? []).map((m) => m.displayFolder).filter((f): f is string => !!f))), [spec]);
  // Measures whose table no longer exists (e.g. after a JSON edit) — surfaced honestly rather than vanishing.
  const orphanMeasures = useMemo(() => (spec.measures ?? [])
    .map((m, gi) => ({ m, gi }))
    .filter((x) => !tableNames.includes(x.m.table)), [spec, tableNames]);

  return (
    <div className="p-4 flex flex-col gap-4">
      {/* shared native-autocomplete sources for the format + display-folder inputs */}
      <datalist id="spec-formats">{FORMAT_PRESETS.filter((p) => p.value).map((p) => <option key={p.value} value={p.value}>{p.label}</option>)}</datalist>
      <datalist id="spec-folders">{folderSuggestions.map((f) => <option key={f} value={f} />)}</datalist>

      {/* Header */}
      <div className="flex items-baseline gap-3 flex-wrap">
        <NameInput value={spec.name ?? ''} placeholder="Untitled model" ariaLabel="Model name"
          onCommit={(v) => edit((s) => { s.name = v; })}
          className="text-lg font-bold" style={{ background: 'transparent', border: '1px solid transparent', borderRadius: 4, padding: '1px 4px', minWidth: 160 }} />
        <Chip>{spec.storageMode === 'directLake' ? 'Direct Lake' : 'Import'}</Chip>
        <Chip>CL {spec.compatibilityLevel ?? 1604}</Chip>
        {spec.source?.server && <span className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>{spec.source.server}{spec.source.database ? ' · ' + spec.source.database : ''}</span>}
        {counts && <span className="ml-auto text-[12px]" style={{ color: 'var(--sem-muted)' }}>{counts.tables} tables ({counts.facts} fact · {counts.dims} dim) · {counts.relationships} relationships · {counts.measures} measures</span>}
      </div>

      {/* Tables */}
      <Section title={`Tables (${spec.tables?.length ?? 0})`} action={<TableAdder spec={spec} edit={edit} />}>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
          {(spec.tables ?? []).map((t, ti) => (
            <TableCard key={ti} t={t} ti={ti} spec={spec} edit={edit} daxModel={daxModel} folderSuggestions={folderSuggestions} />
          ))}
        </div>
        {(spec.tables?.length ?? 0) === 0 && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No tables yet. Add a table, or add a measure to a new measure group.</div>}
      </Section>

      {/* Orphaned measures (their table was removed/renamed) */}
      {orphanMeasures.length > 0 && (
        <Section title={`Unassigned measures (${orphanMeasures.length})`}>
          <div className="rounded-lg p-3 flex flex-col gap-1.5" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-bad)' }}>
            <div className="text-[11px]" style={{ color: 'var(--sem-bad)' }}>These measures point at a table that no longer exists. Reassign a table or remove them.</div>
            {orphanMeasures.map(({ m, gi }) => (
              <div key={gi} className="flex items-center gap-2 text-[12px]">
                <span style={{ color: 'var(--sem-accent)' }}>{m.name}</span>
                <span className="truncate" style={{ color: 'var(--sem-muted)' }}>was on '{m.table}'</span>
                <div className="ml-auto flex items-center gap-1">
                  <Sel value="" ariaLabel="Reassign table" onChange={(v) => { if (v) edit((s) => { s.measures[gi].table = v; }); }}
                    options={[{ value: '', label: 'reassign to…' }, ...tableNames.map((n) => ({ value: n, label: n }))]} />
                  <IconBtn title="Remove measure" onClick={() => edit((s) => { s.measures.splice(gi, 1); })}>✕</IconBtn>
                </div>
              </div>
            ))}
          </div>
        </Section>
      )}

      {/* Relationships */}
      <Section title={`Relationships (${spec.relationships?.length ?? 0})`}>
        <RelationshipsEditor spec={spec} edit={edit} />
      </Section>

      {/* Time intelligence + date table (read-only summary — authored via the Advanced Modelling / Calendars tabs) */}
      {((spec.timeIntelligence?.length ?? 0) > 0 || spec.dateTable) && (
        <Section title="Time intelligence">
          <div className="flex items-center gap-2 flex-wrap text-[12px]">
            {spec.dateTable && <Chip>date table: {spec.dateTable.name}</Chip>}
            {(spec.timeIntelligence ?? []).map((v) => <Badge key={v} color="#4E7CA8">{v}</Badge>)}
            {(spec.timeIntelligenceBaseMeasures?.length ?? 0) > 0 && <span style={{ color: 'var(--sem-muted)' }}>over {spec.timeIntelligenceBaseMeasures!.join(', ')}</span>}
          </div>
        </Section>
      )}
    </div>
  );
}

// ---- one table card: name/role/binding + editable columns + its measures --------------------------
function TableCard({ t, ti, spec, edit, daxModel, folderSuggestions }: {
  t: SpecTable; ti: number; spec: ModelSpec; edit: (mut: (s: ModelSpec) => void) => void; daxModel: DaxModel; folderSuggestions: string[];
}) {
  const cols = t.columns ?? [];
  const measuresOnly = cols.length === 0;
  const tableMeasures = (spec.measures ?? []).map((m, gi) => ({ m, gi })).filter((x) => x.m.table === t.name);

  // Rename cascades to the measures + relationships that reference the table by name (spec references are strings).
  // A duplicate name makes an invalid spec (the builder skips the 2nd as "already exists"), so a collision is auto-
  // disambiguated with a numeric suffix against the OTHER tables; the field then shows the applied name (honest, no throw).
  const renameTable = (newName: string) => edit((s) => {
    const old = s.tables[ti].name;
    if (!newName || newName === old) return;
    const others = s.tables.filter((_, i) => i !== ti).map((t) => t.name);
    const finalName = uniqueName(others, newName);
    if (finalName === old) return;
    s.tables[ti].name = finalName;
    for (const m of s.measures ?? []) if (m.table === old) m.table = finalName;
    for (const r of s.relationships ?? []) { if (r.fromTable === old) r.fromTable = finalName; if (r.toTable === old) r.toTable = finalName; }
  });
  // Deleting a table cascades its measures (a measure cannot exist without its table) + any relationship touching it,
  // so the saved spec stays valid. It is all recoverable via Discard until Save.
  const deleteTable = () => edit((s) => {
    const name = s.tables[ti].name;
    s.tables.splice(ti, 1);
    s.measures = (s.measures ?? []).filter((m) => m.table !== name);
    s.relationships = (s.relationships ?? []).filter((r) => r.fromTable !== name && r.toTable !== name);
  });
  const addColumn = () => edit((s) => {
    const table = s.tables[ti];
    table.columns = table.columns ?? [];
    table.columns.push({ name: uniqueName(table.columns.map((c) => c.name), 'Column'), dataType: 'String' });
  });
  const addMeasure = () => edit((s) => {
    s.measures = s.measures ?? [];
    s.measures.push({ table: s.tables[ti].name, name: uniqueName((s.measures ?? []).map((m) => m.name), 'New measure'), dax: '' });
  });

  return (
    <div className="rounded-lg p-3" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-2 mb-1.5 flex-wrap">
        <NameInput value={t.name} ariaLabel="Table name" onCommit={renameTable}
          className="font-semibold" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '1px 6px', minWidth: 120 }} />
        <span title="Star-schema role" style={{ display: 'inline-flex', width: 6, height: 6, borderRadius: 6, background: ROLE_COLOR[t.role ?? 'isolated'] ?? 'var(--sem-muted)' }} />
        <Sel value={t.role ?? 'isolated'} ariaLabel="Table role" onChange={(v) => edit((s) => { s.tables[ti].role = v; })}
          options={TABLE_ROLES.map((r) => ({ value: r, label: r }))} />
        {measuresOnly && <Badge color="var(--sem-muted)">measures only</Badge>}
        <IconBtn title="Delete table" onClick={deleteTable}>✕</IconBtn>
      </div>

      {/* source binding (Direct Lake entity / schema / source) */}
      <div className="flex items-center gap-1.5 mb-2 flex-wrap text-[11px]">
        <FieldMini label="entity" value={t.entity ?? ''} onCommit={(v) => edit((s) => { s.tables[ti].entity = v || undefined; })} />
        <FieldMini label="schema" value={t.schema ?? ''} onCommit={(v) => edit((s) => { s.tables[ti].schema = v || undefined; })} />
        <FieldMini label="source" value={t.sourceName ?? ''} onCommit={(v) => edit((s) => { s.tables[ti].sourceName = v || undefined; })} />
      </div>

      {/* columns */}
      <div className="flex flex-col gap-1">
        {cols.map((c, ci) => (
          <ColumnRow key={ci} c={c} spec={spec}
            onName={(v) => edit((s) => {
              const table = s.tables[ti]; const old = table.columns[ci].name; table.columns[ci].name = v;
              for (const r of s.relationships ?? []) { if (r.fromTable === table.name && r.fromColumn === old) r.fromColumn = v; if (r.toTable === table.name && r.toColumn === old) r.toColumn = v; }
            })}
            onType={(v) => edit((s) => { s.tables[ti].columns[ci].dataType = v; })}
            onSummarize={(v) => edit((s) => { s.tables[ti].columns[ci].summarizeBy = v === 'None' ? undefined : v; })}
            onKey={(v) => edit((s) => { s.tables[ti].columns[ci].isKey = v || undefined; })}
            onHidden={(v) => edit((s) => { s.tables[ti].columns[ci].hidden = v || undefined; })}
            onDelete={() => edit((s) => { s.tables[ti].columns.splice(ci, 1); })} />
        ))}
        {measuresOnly && <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{t.calculatedExpression ? 'calculated (columns inferred)' : 'no columns'}</span>}
        {!t.calculatedExpression && <button onClick={addColumn} className="self-start text-[11px] mt-0.5" style={{ color: 'var(--sem-accent)' }}>+ Column</button>}
      </div>

      {/* measures on this table */}
      <div className="mt-2 pt-2" style={{ borderTop: '1px solid var(--sem-border)' }}>
        {tableMeasures.map(({ m, gi }) => (
          <MeasureRow key={gi} m={m} daxModel={daxModel}
            onName={(v) => edit((s) => { s.measures[gi].name = v; })}
            onDax={(v) => edit((s) => { s.measures[gi].dax = v; })}
            onFormat={(v) => edit((s) => { s.measures[gi].formatString = v || undefined; })}
            onFolder={(v) => edit((s) => { s.measures[gi].displayFolder = v || undefined; })}
            onDelete={() => edit((s) => { s.measures.splice(gi, 1); })} />
        ))}
        <button onClick={addMeasure} className="text-[11px] mt-1" style={{ color: 'var(--sem-accent)' }}>+ Measure</button>
      </div>
    </div>
  );
}

function ColumnRow({ c, spec, onName, onType, onSummarize, onKey, onHidden, onDelete }: {
  c: SpecColumn; spec: ModelSpec; onName: (v: string) => void; onType: (v: string) => void; onSummarize: (v: string) => void; onKey: (v: boolean) => void; onHidden: (v: boolean) => void; onDelete: () => void;
}) {
  void spec;
  const typeOptions = SPEC_DATA_TYPES.includes(c.dataType ?? '') || !c.dataType ? SPEC_DATA_TYPES : [c.dataType, ...SPEC_DATA_TYPES];
  return (
    <div className="flex items-center gap-1.5 text-[12px] flex-wrap">
      <NameInput value={c.name} ariaLabel="Column name" onCommit={onName}
        style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '1px 6px', minWidth: 120, color: c.hidden ? 'var(--sem-muted)' : 'var(--sem-fg)' }} />
      <Sel value={c.dataType ?? 'String'} ariaLabel="Data type" onChange={onType} options={typeOptions.map((d) => ({ value: d, label: d }))} />
      <Sel value={c.summarizeBy ?? 'None'} ariaLabel="Default summarization" title="Default summarization" onChange={onSummarize} options={SUMMARIZE_BY.map((d) => ({ value: d, label: d === 'None' ? 'Σ none' : 'Σ ' + d }))} />
      <Toggle on={!!c.isKey} onToggle={onKey} title="Key column">key</Toggle>
      <Toggle on={!!c.hidden} onToggle={onHidden} title="Hidden">hidden</Toggle>
      <div className="ml-auto"><IconBtn title="Remove column" onClick={onDelete}>✕</IconBtn></div>
    </div>
  );
}

function MeasureRow({ m, daxModel, onName, onDax, onFormat, onFolder, onDelete }: {
  m: SpecMeasure; daxModel: DaxModel; onName: (v: string) => void; onDax: (v: string) => void; onFormat: (v: string) => void; onFolder: (v: string) => void; onDelete: () => void;
}) {
  return (
    <div className="flex flex-col gap-1 py-1.5" style={{ borderTop: '1px dashed var(--sem-border)' }}>
      <div className="flex items-center gap-1.5 flex-wrap text-[12px]">
        <NameInput value={m.name} ariaLabel="Measure name" onCommit={onName}
          className="font-medium" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '1px 6px', minWidth: 140, color: 'var(--sem-accent)' }} />
        <label className="flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>format
          <NameInput value={m.formatString ?? ''} ariaLabel="Format string" list="spec-formats" placeholder="(default)" onCommit={onFormat}
            style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '1px 6px', width: 110 }} /></label>
        <label className="flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>folder
          <NameInput value={m.displayFolder ?? ''} ariaLabel="Display folder" list="spec-folders" placeholder="(none)" onCommit={onFolder}
            style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '1px 6px', width: 140 }} /></label>
        <div className="ml-auto"><IconBtn title="Remove measure" onClick={onDelete}>✕</IconBtn></div>
      </div>
      {/* full DAX editor — IntelliSense sourced from the SPEC (model prop), plus the offline lint + validity pill + Ask AI */}
      <DaxField value={m.dax ?? ''} onChange={onDax} model={daxModel} table={m.table} minHeight={64}
        placeholder="DAX expression, e.g. SUM ( Sales[SalesAmount] )" ariaLabel={`DAX for ${m.name}`} askContext={`the measure '${m.name}'`} />
    </div>
  );
}

// ---- relationships: edit existing, build new, auto-detect ----------------------------------------
function RelationshipsEditor({ spec, edit }: { spec: ModelSpec; edit: (mut: (s: ModelSpec) => void) => void }) {
  const [building, setBuilding] = useState(false);
  const [proposals, setProposals] = useState<SpecRelationship[] | null>(null);
  const rels = spec.relationships ?? [];

  const detect = () => {
    const found = detectRelationships(spec);
    setProposals(found);
  };
  const accept = (r: SpecRelationship) => {
    edit((s) => { s.relationships = s.relationships ?? []; s.relationships.push(r); });
    setProposals((prev) => (prev ?? []).filter((p) => relKey(p) !== relKey(r)));
  };
  const acceptAll = () => {
    const all = proposals ?? [];
    edit((s) => { s.relationships = [...(s.relationships ?? []), ...all]; });
    setProposals([]);
  };

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center gap-2 flex-wrap">
        <button onClick={() => setBuilding((v) => !v)} className="text-[11px] px-2 py-0.5 rounded" style={{ color: 'var(--sem-accent)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>+ Relationship</button>
        <button onClick={detect} className="text-[11px] px-2 py-0.5 rounded" style={{ color: 'var(--sem-fg)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>Detect relationships</button>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>from → to · cardinality defaults to many-to-one</span>
      </div>

      {building && <RelationshipBuilder spec={spec} onAdd={(r) => { edit((s) => { s.relationships = s.relationships ?? []; s.relationships.push(r); }); setBuilding(false); }} onCancel={() => setBuilding(false)} />}

      {proposals && (
        <div className="rounded-lg p-2.5 flex flex-col gap-1.5" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-accent)' }}>
          <div className="flex items-center gap-2 text-[11px]">
            <span className="font-semibold">{proposals.length > 0 ? `Detected ${proposals.length} proposal${proposals.length === 1 ? '' : 's'}` : 'No high-confidence relationships found'}</span>
            <span style={{ color: 'var(--sem-muted)' }}>by key-column name matching: review before accepting</span>
            <div className="ml-auto flex items-center gap-1.5">
              {proposals.length > 0 && <button onClick={acceptAll} className="text-[11px] px-1.5 py-0.5 rounded" style={{ color: '#000', background: 'var(--sem-accent)' }}>Add all</button>}
              <IconBtn title="Dismiss" onClick={() => setProposals(null)}>✕</IconBtn>
            </div>
          </div>
          {proposals.map((r, i) => (
            <div key={i} className="flex items-center gap-1.5 text-[12px]">
              <RelText r={r} />
              <button onClick={() => accept(r)} className="ml-auto text-[11px] px-1.5 py-0.5 rounded" style={{ color: 'var(--sem-accent)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>Add</button>
            </div>
          ))}
        </div>
      )}

      {rels.length === 0 && !building && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No relationships. Build one, or use Detect relationships (helpful right after a Fabric import).</div>}
      <div className="flex flex-col gap-1">
        {rels.map((r, ri) => (
          <RelationshipRow key={ri} r={r} spec={spec}
            onChange={(patch) => edit((s) => { Object.assign(s.relationships[ri], patch); })}
            onDelete={() => edit((s) => { s.relationships.splice(ri, 1); })} />
        ))}
      </div>
    </div>
  );
}

function RelationshipRow({ r, spec, onChange, onDelete }: { r: SpecRelationship; spec: ModelSpec; onChange: (patch: Partial<SpecRelationship>) => void; onDelete: () => void }) {
  const tables = (spec.tables ?? []).map((t) => t.name);
  const colsOf = (name: string) => ((spec.tables ?? []).find((t) => t.name === name)?.columns ?? []).map((c) => c.name);
  const fromOk = tables.includes(r.fromTable) && colsOf(r.fromTable).includes(r.fromColumn);
  const toOk = tables.includes(r.toTable) && colsOf(r.toTable).includes(r.toColumn);
  return (
    <div className="flex items-center gap-1 text-[12px] flex-wrap py-0.5">
      <Sel value={r.fromTable} ariaLabel="From table" onChange={(v) => onChange({ fromTable: v })} options={tables.map((n) => ({ value: n, label: n }))} />
      <Sel value={r.fromColumn} ariaLabel="From column" onChange={(v) => onChange({ fromColumn: v })} options={colsOf(r.fromTable).map((n) => ({ value: n, label: n }))} />
      <span style={{ color: 'var(--sem-accent)' }}>→</span>
      <Sel value={r.toTable} ariaLabel="To table" onChange={(v) => onChange({ toTable: v })} options={tables.map((n) => ({ value: n, label: n }))} />
      <Sel value={r.toColumn} ariaLabel="To column" onChange={(v) => onChange({ toColumn: v })} options={colsOf(r.toTable).map((n) => ({ value: n, label: n }))} />
      <Sel value={normCardinality(r.cardinality)} ariaLabel="Cardinality" title="Relationship cardinality (from → to)" onChange={(v) => onChange({ cardinality: normCardinality(v) })} options={CARDINALITY} />
      <Sel value={r.crossFilter ?? 'OneDirection'} ariaLabel="Cross-filter" title="Cross-filter direction" onChange={(v) => onChange({ crossFilter: v })} options={CROSSFILTER} />
      <Toggle on={r.isActive !== false} onToggle={(v) => onChange({ isActive: v })} title="Active relationship">active</Toggle>
      {(!fromOk || !toOk) && <span className="text-[10px]" style={{ color: 'var(--sem-bad)' }} title="One endpoint no longer resolves to a table/column in this spec">unresolved</span>}
      <div className="ml-auto"><IconBtn title="Remove relationship" onClick={onDelete}>✕</IconBtn></div>
    </div>
  );
}

function RelationshipBuilder({ spec, onAdd, onCancel }: { spec: ModelSpec; onAdd: (r: SpecRelationship) => void; onCancel: () => void }) {
  const tables = (spec.tables ?? []).map((t) => t.name);
  const colsOf = (name: string) => ((spec.tables ?? []).find((t) => t.name === name)?.columns ?? []).map((c) => c.name);
  const [fromTable, setFromTable] = useState(tables[0] ?? '');
  const [fromColumn, setFromColumn] = useState(colsOf(tables[0] ?? '')[0] ?? '');
  const [toTable, setToTable] = useState(tables[1] ?? tables[0] ?? '');
  const [toColumn, setToColumn] = useState(colsOf(tables[1] ?? tables[0] ?? '')[0] ?? '');
  const [cardinality, setCardinality] = useState('manyToOne');
  const [crossFilter, setCrossFilter] = useState('OneDirection');
  const [isActive, setIsActive] = useState(true);

  const existing = new Set((spec.relationships ?? []).map((r) => unordered(`${r.fromTable}.${r.fromColumn}`, `${r.toTable}.${r.toColumn}`)));
  const dup = existing.has(unordered(`${fromTable}.${fromColumn}`, `${toTable}.${toColumn}`));
  const valid = fromTable && fromColumn && toTable && toColumn && fromTable !== toTable && !dup;

  return (
    <div className="rounded-lg p-2.5 flex flex-col gap-2" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-1.5 flex-wrap text-[12px]">
        <span style={{ color: 'var(--sem-muted)' }}>from</span>
        <Sel value={fromTable} ariaLabel="From table" onChange={(v) => { setFromTable(v); setFromColumn(colsOf(v)[0] ?? ''); }} options={tables.map((n) => ({ value: n, label: n }))} />
        <Sel value={fromColumn} ariaLabel="From column" onChange={setFromColumn} options={colsOf(fromTable).map((n) => ({ value: n, label: n }))} />
        <span style={{ color: 'var(--sem-accent)' }}>→</span>
        <span style={{ color: 'var(--sem-muted)' }}>to</span>
        <Sel value={toTable} ariaLabel="To table" onChange={(v) => { setToTable(v); setToColumn(colsOf(v)[0] ?? ''); }} options={tables.map((n) => ({ value: n, label: n }))} />
        <Sel value={toColumn} ariaLabel="To column" onChange={setToColumn} options={colsOf(toTable).map((n) => ({ value: n, label: n }))} />
        <Sel value={cardinality} ariaLabel="Cardinality" title="Relationship cardinality (from → to)" onChange={setCardinality} options={CARDINALITY} />
        <Sel value={crossFilter} ariaLabel="Cross-filter" title="Cross-filter direction" onChange={setCrossFilter} options={CROSSFILTER} />
        <Toggle on={isActive} onToggle={setIsActive} title="Active relationship">active</Toggle>
      </div>
      <div className="flex items-center gap-2">
        <button disabled={!valid} onClick={() => onAdd({ fromTable, fromColumn, toTable, toColumn, cardinality, crossFilter, isActive })}
          className="text-[11px] px-2 py-0.5 rounded disabled:opacity-50" style={{ color: '#000', background: 'var(--sem-accent)' }}>Add relationship</button>
        <button onClick={onCancel} className="text-[11px] px-2 py-0.5 rounded" style={{ color: 'var(--sem-fg)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>Cancel</button>
        {dup && <span className="text-[11px]" style={{ color: 'var(--sem-warn)' }}>That relationship already exists.</span>}
        {fromTable === toTable && <span className="text-[11px]" style={{ color: 'var(--sem-warn)' }}>Pick two different tables.</span>}
      </div>
    </div>
  );
}

function RelText({ r }: { r: SpecRelationship }) {
  return (
    <span>{r.fromTable}<span style={{ color: 'var(--sem-muted)' }}>[{r.fromColumn}]</span>
      <span style={{ color: 'var(--sem-accent)' }}> → </span>
      {r.toTable}<span style={{ color: 'var(--sem-muted)' }}>[{r.toColumn}]</span></span>
  );
}

// ---- add-table (incl. a measure group) ------------------------------------------------------------
function TableAdder({ spec, edit }: { spec: ModelSpec; edit: (mut: (s: ModelSpec) => void) => void }) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  const [role, setRole] = useState('dimension');
  const add = () => {
    const nm = name.trim();
    edit((s) => {
      s.tables = s.tables ?? [];
      // Always disambiguate: a typed name that duplicates an existing table makes an invalid spec (the builder skips
      // the 2nd), so suffix it rather than let a collision through.
      const finalName = uniqueName(s.tables.map((t) => t.name), nm || (role === '__measuregroup__' ? 'Measures' : 'Table'));
      const table: SpecTable = role === '__measuregroup__'
        // A measure group is a LOCAL CALCULATED table ({ BLANK() }) to park measures on; a plain column-less import
        // table would build with an unresolvable default M partition (never materialises as a valid measures table).
        ? { name: finalName, role: 'isolated', calculatedExpression: '{ BLANK() }', columns: [] }
        : { name: finalName, role, columns: [] };
      s.tables.push(table);
    });
    setName(''); setRole('dimension'); setOpen(false);
  };
  if (!open) return <button onClick={() => setOpen(true)} className="text-[11px] px-2 py-0.5 rounded" style={{ color: 'var(--sem-accent)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>+ Table</button>;
  return (
    <span className="inline-flex items-center gap-1.5">
      <input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="table name…"
        onKeyDown={(e) => { if (e.key === 'Enter') add(); else if (e.key === 'Escape') setOpen(false); }}
        className="text-[12px] px-2 py-0.5 rounded outline-none" style={{ width: 150, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      <Sel value={role} ariaLabel="New table role" onChange={setRole}
        options={[...TABLE_ROLES.map((r) => ({ value: r, label: r })), { value: '__measuregroup__', label: 'Measure group' }]} />
      <button onClick={add} className="text-[11px] px-1.5 py-0.5 rounded" style={{ color: '#000', background: 'var(--sem-accent)' }}>Add</button>
      <IconBtn title="Cancel" onClick={() => setOpen(false)}>✕</IconBtn>
    </span>
  );
}

// ---- the "Detect relationships" heuristic (conservative, client-side, no engine call) -------------
// Proposes many→one relationships by high-confidence KEY-COLUMN NAME matching: a foreign-key column (name ending in
// Key/Id, and NOT the table's own identity key) that maps to exactly one other table's primary-key column — either an
// identically-named key column that is a primary-key candidate there, or a table whose NAME equals the stripped base
// (ProductKey → table Product). Only unambiguous single-target matches are proposed, deduped against existing rels.
function detectRelationships(spec: ModelSpec): SpecRelationship[] {
  const tables = spec.tables ?? [];
  const existing = new Set((spec.relationships ?? []).map((r) => unordered(`${r.fromTable}.${r.fromColumn}`, `${r.toTable}.${r.toColumn}`)));
  const seen = new Set<string>();
  const proposals: SpecRelationship[] = [];
  const lc = (s: string) => (s ?? '').toLowerCase();
  const isKeyName = (n: string) => /(?:key|id)$/i.test(n);
  const stripKey = (n: string) => n.replace(/(?:key|id)$/i, '');
  const pkCandidates = (t: SpecTable) => (t.columns ?? []).filter((c) => c.isKey || lc(c.name) === lc(t.name) + 'key' || lc(c.name) === lc(t.name) + 'id' || lc(c.name) === lc(t.name));

  for (const from of tables) {
    for (const c of from.columns ?? []) {
      if (!isKeyName(c.name)) continue;
      // Skip a table's OWN identity key — that makes `from` the one/lookup side, not the many/foreign-key side.
      if (lc(c.name) === lc(from.name) + 'key' || lc(c.name) === lc(from.name) + 'id' || lc(c.name) === lc(from.name)) continue;
      const base = lc(stripKey(c.name));
      const targets: { table: string; col: string }[] = [];
      for (const t of tables) {
        if (t.name === from.name) continue;
        const pks = pkCandidates(t);
        const exact = pks.find((pk) => lc(pk.name) === lc(c.name));
        if (exact) { targets.push({ table: t.name, col: exact.name }); continue; }
        if (base && lc(t.name) === base && pks.length) targets.push({ table: t.name, col: pks[0].name });
      }
      const distinct = Array.from(new Set(targets.map((x) => x.table)));
      if (distinct.length !== 1) continue;   // ambiguous or none — stay conservative
      const tgt = targets.find((x) => x.table === distinct[0])!;
      const key = unordered(`${from.name}.${c.name}`, `${tgt.table}.${tgt.col}`);
      if (existing.has(key) || seen.has(key)) continue;
      seen.add(key);
      proposals.push({ fromTable: from.name, fromColumn: c.name, toTable: tgt.table, toColumn: tgt.col, crossFilter: 'OneDirection', isActive: true });
    }
  }
  return proposals;
}
const unordered = (a: string, b: string) => [a, b].sort().join('~');
const relKey = (r: SpecRelationship) => `${r.fromTable}.${r.fromColumn}->${r.toTable}.${r.toColumn}`;

function BuildReport({ report, onClose }: { report: SpecBuildReport; onClose: () => void }) {
  const tone = report.errors.length > 0 ? 'warn' : 'good';
  return (
    <div className="px-4 py-2 border-b text-[12px]" style={{ borderColor: 'var(--sem-border)', background: 'color-mix(in srgb,var(--sem-' + (tone === 'good' ? 'good' : 'warn') + ') 10%, transparent)' }}>
      <div className="flex items-center gap-3 flex-wrap">
        <span className="font-semibold">Built:</span>
        <span><b className="tnum">{report.created.length}</b> created</span>
        <span style={{ color: 'var(--sem-muted)' }}><b className="tnum">{report.skipped.length}</b> skipped</span>
        {report.errors.length > 0 && <span style={{ color: 'var(--sem-bad)' }}><b className="tnum">{report.errors.length}</b> errors</span>}
        <span style={{ color: 'var(--sem-muted)' }}>tables {report.tablesBefore}→{report.tablesAfter} · measures {report.measuresBefore}→{report.measuresAfter}</span>
        <button onClick={onClose} className="ml-auto" style={{ color: 'var(--sem-muted)' }}>✕</button>
      </div>
      {report.errors.length > 0 && <div className="mt-1" style={{ color: 'var(--sem-bad)' }}>{report.errors.slice(0, 5).map((e, i) => <div key={i}>· {e}</div>)}</div>}
    </div>
  );
}

function SpecWizard({ session, busy, onAutogen, onSql, onBlank, onLoad }: {
  session?: { modelName?: string; tables?: number; measures?: number } | null;
  busy: string | null; onAutogen: () => void; onSql: () => void; onBlank: () => void; onLoad: () => void;
}) {
  const hasObjects = (session?.tables ?? 0) > 0 || (session?.measures ?? 0) > 0;
  return (
    <div className="sem-evidence-page h-full flex items-center justify-center p-8">
      <div className="w-full max-w-[880px] rounded-lg border p-6" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
        <div className="text-[10px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-accent)' }}>{hasObjects ? 'Model Spec wizard' : 'New model wizard'} · Step 1</div>
        <div className="mt-1 text-[18px] font-semibold">Create the first draft</div>
        <div className="mt-2 max-w-[680px] text-[12px]" style={{ color: 'var(--sem-muted)' }}>
          Choose a starting point. The wizard creates a Model Spec, not the finished model. You and AI Assistant review and edit the same draft here, then Build into model when it is ready.
        </div>
        <div className="mt-5 grid gap-3 md:grid-cols-2">
          {hasObjects && <WizardChoice title={`Draft from ${session?.modelName || 'the open model'}`} detail="Read the current model into a first draft. The model is not changed." onClick={onAutogen} busy={busy === 'model'} primary />}
          <WizardChoice title="Start from scratch" detail="Begin with an empty structure and add tables, relationships and measures during review." onClick={onBlank} busy={busy === 'blank'} primary={!hasObjects} />
          <WizardChoice title="Draft from a SQL source" detail="Read table and column metadata from an endpoint. No source data is copied." onClick={onSql} />
          <WizardChoice title="Open a saved Model Spec" detail="Continue a versioned JSON draft from this project or another repository." onClick={onLoad} busy={busy === 'load'} />
        </div>
        <div className="mt-5 border-t pt-4 text-[11px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
          <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>Step 2: Review and build.</span> Nothing is published by this wizard. Build is one undoable model change; publishing remains a separate reviewed action.
        </div>
      </div>
    </div>
  );
}

function WizardChoice({ title, detail, onClick, busy, primary }: { title: string; detail: string; onClick: () => void; busy?: boolean; primary?: boolean }) {
  return (
    <button onClick={onClick} disabled={busy} className="rounded-lg border p-4 text-left disabled:opacity-50"
      style={{ borderColor: primary ? 'var(--sem-accent)' : 'var(--sem-border)', background: primary ? 'var(--sem-accent-soft)' : 'var(--sem-surface-2)', color: 'var(--sem-fg)' }}>
      <div className="text-[13px] font-semibold">{busy ? 'Working…' : title}</div>
      <div className="mt-1 text-[11px] leading-relaxed" style={{ color: 'var(--sem-muted)' }}>{detail}</div>
    </button>
  );
}

// ---- little styled atoms (match the other tabs' --sem-* + Tailwind conventions) ----
function Section({ title, action, children }: { title: string; action?: React.ReactNode; children: React.ReactNode }) {
  return (
    <div>
      <div className="flex items-center gap-2 mb-2">
        <div className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>{title}</div>
        {action && <div className="ml-auto">{action}</div>}
      </div>
      {children}
    </div>
  );
}
// A commit-on-blur text input: holds its own buffer while typing (so cursor/focus never jump on the parent re-render)
// and only fires onCommit on blur/Enter. Escape reverts. Optional `list` wires a native <datalist> for suggestions.
function NameInput({ value, onCommit, placeholder, className, style, ariaLabel, list }: {
  value: string; onCommit: (v: string) => void; placeholder?: string; className?: string; style?: React.CSSProperties; ariaLabel?: string; list?: string;
}) {
  const [v, setV] = useState(value);
  const reverting = useRef(false);   // Escape sets this so the ensuing blur reverts instead of committing (setV is async, so onBlur can't just read v)
  useEffect(() => { setV(value); }, [value]);
  const commit = () => { if (reverting.current) { reverting.current = false; setV(value); return; } if (v !== value) onCommit(v); };
  return (
    <input value={v} onChange={(e) => setV(e.target.value)} onBlur={commit} placeholder={placeholder} aria-label={ariaLabel} list={list}
      onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); else if (e.key === 'Escape') { reverting.current = true; (e.target as HTMLInputElement).blur(); } }}
      className={'outline-none ' + (className ?? '')} style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontSize: 12, ...style }} />
  );
}
function FieldMini({ label, value, onCommit }: { label: string; value: string; onCommit: (v: string) => void }) {
  return (
    <label className="inline-flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>{label}
      <NameInput value={value} ariaLabel={label} placeholder="Optional" onCommit={onCommit}
        style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '0 5px', width: 96, color: 'var(--sem-fg)' }} />
    </label>
  );
}
function Sel({ value, onChange, options, title, ariaLabel }: { value: string; onChange: (v: string) => void; options: { value: string; label: string }[]; title?: string; ariaLabel?: string }) {
  // When `value` isn't one of the options (an unresolved column after a rename/delete), a controlled <select> would
  // otherwise DISPLAY the first option and silently coerce the value on the next change. Carry the stray value in a
  // disabled placeholder so it shows as-is and reads invalid; no silent coercion, and the invalid affordance stays honest.
  const missing = value !== '' && value != null && !options.some((o) => o.value === value);
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} aria-label={ariaLabel}
      title={missing ? (title ? title + ' (unresolved: ' + value + ')' : 'unresolved: ' + value) : title}
      style={{ background: 'var(--sem-surface)', color: missing ? 'var(--sem-bad)' : 'var(--sem-fg)', border: '1px solid ' + (missing ? 'var(--sem-bad)' : 'var(--sem-border)'), borderRadius: 4, padding: '1px 4px', fontSize: 12 }}>
      {missing && <option value={value} disabled>{value}</option>}
      {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
    </select>
  );
}
function Toggle({ on, onToggle, title, children }: { on: boolean; onToggle: (v: boolean) => void; title?: string; children: React.ReactNode }) {
  return (
    <button onClick={() => onToggle(!on)} title={title} className="text-[10px] px-1.5 py-0.5 rounded-md font-medium"
      style={on ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>{children}</button>
  );
}
function IconBtn({ onClick, title, children }: { onClick: () => void; title?: string; children: React.ReactNode }) {
  return (
    <button onClick={onClick} title={title} className="text-[12px] px-1.5 rounded leading-none"
      style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>{children}</button>
  );
}
function Btn({ children, onClick, primary, busy, disabled, title }: { children: React.ReactNode; onClick: () => void; primary?: boolean; busy?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={busy || disabled} title={title}
      className="px-2.5 py-1 rounded text-[12px] font-medium disabled:opacity-50"
      style={{ background: primary ? 'var(--sem-accent)' : 'var(--sem-surface-2)', color: primary ? '#000' : 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {busy ? '…' : children}
    </button>
  );
}
function Chip({ children }: { children: React.ReactNode }) {
  return <span className="text-[11px] px-2 py-0.5 rounded-full" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{children}</span>;
}
function Badge({ color, children }: { color: string; children: React.ReactNode }) {
  return <span className="text-[9px] px-1 rounded align-middle" style={{ background: color, color: '#000' }}>{children}</span>;
}
function Banner({ tone, children }: { tone: 'bad' | 'good' | 'warn'; children: React.ReactNode }) {
  const c = tone === 'bad' ? 'var(--sem-bad)' : tone === 'warn' ? 'var(--sem-warn)' : 'var(--sem-good)';
  return <div className="mx-4 my-2 rounded px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,' + c + ' 14%, transparent)', color: c }}>{children}</div>;
}
function Field({ label, value, onChange, placeholder, wide }: { label: string; value: string; onChange: (v: string) => void; placeholder?: string; wide?: boolean }) {
  return (
    <label className="flex flex-col gap-1">
      <span style={{ color: 'var(--sem-muted)' }}>{label}</span>
      <input value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder}
        style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '4px 8px', minWidth: wide ? 320 : 160 }} />
    </label>
  );
}
