import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onSpecChange, pickSpecFile } from './bridge';
import { listSqlSources, openSqlSources, type SqlSourceRecord } from './connectionshub';
import { useConnection } from './connection';
import { SIGN_IN_MODES } from './copy';
import { DaxField } from './daxeditor';
import { EMPTY_MODEL, type DaxModel } from './dax';
import { FORMAT_PRESETS } from './advmodels';
import { DraftMark, EditMovedBanner, useEditBase } from './editbase';
import { draftModel, draftStore, heldDraft, stable, useHasDraft } from './drafts';
import { OrphanDrafts } from './orphandrafts';

// Wire shapes mirror Semanticus.Engine/Spec.cs (camelCased on the wire).
interface SpecColumn { name: string; dataType?: string; sourceColumn?: string; isKey?: boolean; hidden?: boolean; summarizeBy?: string; }
interface SpecTable { name: string; role?: string; entity?: string; schema?: string; mExpression?: string; calculatedExpression?: string; sourceName?: string; columns: SpecColumn[]; }
// `note` is the draft's own plain-words reason for a choice, today only why a link is drafted switched off.
interface SpecRelationship { fromTable: string; fromColumn: string; toTable: string; toColumn: string; cardinality?: string; crossFilter?: string; isActive?: boolean; note?: string; }
interface SpecMeasure { table: string; name: string; dax?: string; formatString?: string; displayFolder?: string; }
interface SpecSource { kind?: string; server?: string; database?: string; schema?: string; }
// `dateColumn` set = the date table is one of the spec's own tables (a source date table), marked on build.
interface SpecDateTable { name?: string; dateColumn?: string; startExpr?: string; endExpr?: string; markAsDate?: boolean; note?: string; }
interface ModelSpec {
  name?: string; compatibilityLevel?: number; storageMode?: string; source?: SpecSource;
  tables: SpecTable[]; relationships: SpecRelationship[]; measures: SpecMeasure[];
  timeIntelligence: string[]; timeIntelligenceBaseMeasures?: string[]; dateTable?: SpecDateTable;
}
/** What the structured view holds while it has unsaved edits: the working copy. It is held with the CONTENT of the spec it was typed over (`base.content`, one string per spec):
 *  a spec version is a per-session counter that starts again at 0 in a restarted engine, so it never says whether the spec moved. */
interface StructDraft { spec: ModelSpec; }
const contentOf = (value?: ModelSpec | null): string => stable(value ?? null);
interface SpecSnapshot { version: number; source: string; spec: ModelSpec | null; }
interface SpecBuildReport { revision: number; created: string[]; skipped: string[]; errors: string[]; notes?: string[]; tablesBefore: number; tablesAfter: number; measuresBefore: number; measuresAfter: number; note?: string; }

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
export function SpecView({ session, startSql, onStartTaken, onStartPlan }: {
  session?: { modelName?: string; tables?: number; measures?: number } | null;
  /** A route asked for the SQL step (route tool 'spec', target SQL_DRAFT_TARGET): a one-use nonce, 0 or absent for none. */
  startSql?: number;
  onStartTaken?: () => void;
  /** The Plan's own answer to a SQL start on an EMPTY model: a start plan (read, decided and approved before anything is
   *  built) instead of this wizard's two steps. A model that already has tables keeps the wizard (lane sf-studio). */
  onStartPlan?: () => void;
}) {
  const [snap, setSnap] = useState<SpecSnapshot | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [report, setReport] = useState<SpecBuildReport | null>(null);
  // Text typed into Edit JSON and not applied outlives this tab (a tab switch, a restarted engine, a reload): the tab opens the JSON
  // editor again on it (join 11, the drafts class, drafts.ts). It ends on an Apply the engine took, Cancel or Hide JSON, and Use the new one.
  const heldJson = heldDraft<string>('spec|json');
  const [editingJson, setEditingJson] = useState(!!heldJson);
  const [jsonDraft, setJsonDraft] = useState(heldJson?.value ?? '');
  // Hide JSON only puts the editor away (F13, drafts-sol): words typed there stay held (`jsonKept`) beside the structured view's own draft, and Edit JSON opens on them. Cancel and an Apply the
  // engine took end them. When the structured view holds edits too, Apply JSON waits for the person to say which one it may use (`bothAcked`), so neither silently replaces the other.
  const [jsonKept, setJsonKept] = useState(false);
  const [bothAcked, setBothAcked] = useState(false);
  // The structured view's inline edits are held the same way (scope `spec|struct`): the working copy and the CONTENT of the spec it was taken from (`base.content`). They end on
  // a Save the engine took, Discard, Load the server version, and any start that replaces the spec (Autogenerate, Clear, Open spec, Apply JSON): `dirty` going false.
  const heldStruct = heldDraft<StructDraft, { content?: string }>('spec|struct');
  const [draft, setDraft] = useState<ModelSpec | null>(heldStruct?.value.spec ?? null);   // the editable working copy
  // The JSON editor saves the whole spec from the text taken when it opened; the assistant refines the same draft
  // (set_spec), so Apply reads it again first and a spec that moved is asked about, never overwritten (UAT stale-copy class).
  const specText = (value?: ModelSpec | null) => JSON.stringify(value ?? {}, null, 2);
  const jsonVersion = useRef<number | null>(null);   // the version the last Apply read: the write names it as the spec it replaces
  const jsonGuard = useEditBase<string>({
    current: snap ? specText(snap.spec) : undefined, draft: jsonDraft, editing: editingJson || jsonKept, reseed: setJsonDraft, persist: { scope: 'spec|json' },
    readNow: async () => { const live = await rpc<SpecSnapshot>('getSpec'); jsonVersion.current = live.version; return specText(live.spec); },
  });
  const [dirty, setDirty] = useState(!!heldStruct);
  // The content of the spec the draft was synced from. Compared with the content the engine holds NOW, never a version: a restarted engine counts versions again from 0.
  const [baseContent, setBaseContent] = useState<string | null>(heldStruct ? heldStruct.base.content ?? '' : null);
  const [showFabric, setShowFabric] = useState(false);
  const [showChoices, setShowChoices] = useState(false);
  const [fabric, setFabric] = useState({ server: '', database: '', storageMode: 'import', authMode: 'azcli', tenant: '' });
  // The SQL endpoints saved on Connections, read each time the SQL step opens. null = still reading.
  const [sqlSources, setSqlSources] = useState<SavedSqlSources>(null);
  const [sqlReadNonce, setSqlReadNonce] = useState(0);
  const { openConnections } = useConnection();
  const reloadTimer = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (!showFabric) return;
    let live = true;
    setSqlSources(null);
    void readSavedSqlSources().then((r) => { if (live) setSqlSources(r); });
    return () => { live = false; };
  }, [showFabric, sqlReadNonce]);
  // Arriving on the SQL step from Connections, Find a tool or the Plan: open it once, then say it was taken.
  useEffect(() => {
    if (!startSql) return;
    if (onStartPlan && session?.tables === 0) { onStartPlan(); onStartTaken?.(); return; }
    setShowChoices(false); setShowFabric(true);
    onStartTaken?.();
  }, [startSql]);  // eslint-disable-line react-hooks/exhaustive-deps

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
  const snapContent = useMemo(() => (snap ? contentOf(snap.spec) : null), [snap]);   // computed once per read, not on every render
  useEffect(() => {
    if (!dirty) { setDraft(snap?.spec ? clone(snap.spec) : null); setBaseContent(snapContent); }
  }, [snap, dirty]);   // eslint-disable-line react-hooks/exhaustive-deps

  // Hold what was edited above the tab, so a tab switch, a restarted engine or a reload keeps it (join 11, the drafts class).
  const draftSig = useMemo(() => stable(draft), [draft]);
  useEffect(() => {
    const m = draftModel();
    if (!m) return;
    if (dirty && draft) draftStore.set(m, 'spec|struct', '', { value: { spec: draft }, base: { content: baseContent ?? '' } });
    else if (!dirty) draftStore.clear(m, 'spec|struct', '');
  }, [dirty, draftSig, baseContent]);   // eslint-disable-line react-hooks/exhaustive-deps

  const spec = draft;
  // The spec the engine holds now is not the one the draft was typed over: compared by content (a version means nothing across an engine restart).
  const serverChanged = dirty && snap != null && baseContent != null && snapContent !== baseContent;
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
    setReport(null);
  };

  async function run(label: string, fn: () => Promise<unknown>) {
    setBusy(label); setErr(null);
    try { await fn(); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }
  const autogenModel = () => run('model', async () => { const next = await rpc<SpecSnapshot>('autogenerateSpecFromModel', 'human'); setReport(null); setDirty(false); setSnap(next); setShowChoices(false); });
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
    setShowFabric(false); setShowChoices(false);
  });
  // The same call, fed from an endpoint saved on Connections: its server, database, sign-in and tenant.
  const draftFromSaved = (r: SqlSourceRecord, storageMode: string) => run('fabric', async () => {
    setReport(null);
    setSnap(await rpc<SpecSnapshot>('autogenerateSpecFromFabric', ...sqlDraftArgs(r, storageMode)));
    setShowFabric(false); setShowChoices(false);
  });
  const build = () => {
    if (dirty) {
      setErr('Save your spec changes first. Build uses the last saved draft, not what you see on screen.');
      setReport(null);
      return;
    }
    void run('build', async () => setReport(await rpc<SpecBuildReport>('buildModelFromSpec', 'human')));
  };
  const clear = () => run('clear', async () => { setReport(null); setDirty(false); setSnap(await rpc<SpecSnapshot>('clearSpec', 'human')); });
  const save = () => run('save', async () => {
    if (!draft) return;
    // The spec is read again first: a draft taken from an older version writes nothing until the person chose (the
    // banner above offers Keep mine or Load the server version), so the assistant's newer spec is never written over.
    const live = await rpc<SpecSnapshot>('getSpec');
    if (baseContent != null && contentOf(live.spec) !== baseContent) {
      setSnap(live);
      setErr('The spec changed elsewhere, so nothing was saved. Choose Keep mine or Load the server version first.');
      return;
    }
    // The content matched, so the write names the version of THIS read, and the engine compares it inside its own write (the read above can still be a moment behind).
    const res = await rpc<SpecSnapshot>('setSpec', JSON.stringify(draft), 'human', live.version);
    setDirty(false); setSnap(res);
  });
  const loadFile = async () => {
    const path = await pickSpecFile('open');
    if (!path) return;
    await run('load', async () => { const next = await rpc<SpecSnapshot>('loadSpec', path, 'human'); setReport(null); setDirty(false); setSnap(next); setShowChoices(false); });
  };
  const saveFile = async () => {
    if (!snap?.spec || dirty) return;
    const suggested = `${(snap.spec.name || session?.modelName || 'model').replace(/[^\w.-]+/g, '_')}.spec.json`;
    const path = await pickSpecFile('save', suggested);
    if (!path) return;
    await run('save-file', async () => { setSnap(await rpc<SpecSnapshot>('saveSpec', path)); });
  };
  const discard = () => { setDirty(false); };   // the sync effect reloads the draft from the server snapshot
  // Opening Edit JSON starts from what the structured view shows, unless words typed there earlier are held: those are the person's and are never replaced by a fresh copy.
  const openJson = () => { if (jsonKept) { setJsonKept(false); setEditingJson(true); return; } setJsonDraft(JSON.stringify(draft ?? {}, null, 2)); setEditingJson(true); };
  const hideJson = () => { setJsonKept(jsonGuard.drafted); setEditingJson(false); };
  const bothHeld = editingJson && dirty && jsonGuard.drafted;
  useEffect(() => { if (!bothHeld) setBothAcked(false); }, [bothHeld]);
  const applyJson = () => run('apply', async () => {
    const submittedJson = jsonDraft;   // the words this Apply carries
    let parsed: ModelSpec;
    try { parsed = JSON.parse(jsonDraft) as ModelSpec; }
    catch (e) { throw new Error('Invalid JSON: ' + (e as Error).message); }
    if (bothHeld && !bothAcked) return;   // the structured view holds edits too: the person says which one Apply may use first
    if (!(await jsonGuard.beforeSave())) return;
    let res: SpecSnapshot;
    try { res = await rpc<SpecSnapshot>('setSpec', JSON.stringify(parsed), 'human', jsonVersion.current); }
    catch (e) { if (await jsonGuard.refused(e)) return; throw e; }   // the spec moved after the read: the banner asks, nothing was written
    // R-B: the JSON editor closes, and its draft ends, only when the words it holds are still exactly the ones the engine took; words typed while Apply was on its way stay, over the saved spec.
    const same = jsonGuard.saved(submittedJson, specText(res.spec));
    setDirty(false); setSnap(res);
    if (same) { setEditingJson(false); setJsonKept(false); }
  });
  const startBlank = () => run('blank', async () => {
    const blank: ModelSpec = { name: session?.modelName || 'New model', compatibilityLevel: 1604, storageMode: 'import', tables: [], relationships: [], measures: [], timeIntelligence: [] };
    setReport(null); setDirty(false); setSnap(await rpc<SpecSnapshot>('setSpec', JSON.stringify(blank), 'human', snap?.spec ? snap.version : null));
    setShowChoices(false);
  });

  return (
    <div className="h-full flex flex-col" style={{ color: 'var(--sem-fg)' }}>
      {/* Toolbar */}
      {/* The Plan hosts the draft inside its own page frame, which already sets the page margin; the strips
          here carry no side padding of their own, or everything would sit twice as far in. */}
      <div className="flex items-center gap-2 pb-2 border-b flex-wrap text-(length:--sem-text-meta)" style={{ borderColor: 'var(--sem-border)' }}>
        <div>
          <div className="font-semibold">Model Spec</div>
          <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>One shared draft for you and your assistant. Review it here before anything is built.</div>
        </div>
        {snap?.spec && <Chip>{SOURCE_LABEL[snap.source] ?? snap.source} · v{snap.version}</Chip>}
        {dirty && <span className="text-(length:--sem-text-label) font-medium" style={{ color: 'var(--sem-warn)' }} title="You have unsaved changes to the spec">● Unsaved changes</span>}
        <div className="flex-1" />
        {(spec || showFabric) && !showChoices && <Btn disabled={busy != null} onClick={() => { setShowChoices(true); setShowFabric(false); }}>Back to start</Btn>}
        {showChoices && spec && <Btn onClick={() => setShowChoices(false)}>Resume draft</Btn>}
        {dirty && <Btn primary onClick={save} busy={busy === 'save'}>Save</Btn>}
        {dirty && <Btn onClick={discard}>Discard</Btn>}
        {spec && !showChoices && <>
          <Btn onClick={() => void loadFile()} busy={busy === 'load'}>Open spec…</Btn>
          <Btn onClick={() => void saveFile()} busy={busy === 'save-file'} disabled={dirty} title={dirty ? 'Save draft changes first' : 'Save this Model Spec as a JSON file'}>Save spec…</Btn>
          <Btn onClick={autogenModel} busy={busy === 'model'}>Autogenerate from model</Btn>
          <Btn onClick={() => setShowFabric((v) => !v)}>Autogenerate from SQL…</Btn>
          <Btn onClick={() => { if (editingJson) hideJson(); else openJson(); }}>{editingJson ? 'Hide JSON' : 'Edit JSON'}</Btn>
          {jsonKept && !editingJson && <DraftMark show />}
          <Btn onClick={clear} busy={busy === 'clear'}>Clear</Btn>
          <Btn primary onClick={() => void build()} busy={busy === 'build'} disabled={!dirty && !spec.tables?.length}
            title={dirty ? 'Save your spec changes first' : 'Adds the reviewed objects in one undoable step. It does not publish.'}>Build into model →</Btn>
        </>}
      </div>

      {showFabric && (
        <SqlSourceStep sources={sqlSources} busy={busy === 'fabric'} onDraft={draftFromSaved}
          onAddOnConnections={() => openSqlSources(openConnections)} onRetry={() => setSqlReadNonce((n) => n + 1)}
          typed={fabric} onTyped={(patch) => setFabric((f) => ({ ...f, ...patch }))} onDraftTyped={autogenFabric} />
      )}

      {err && <Banner tone="bad">{err}</Banner>}
      {serverChanged && (
        <div className="my-2 xe-msg xe-msg-warn" style={{ flexWrap: "wrap" }}>
          <span>{snap?.spec
            ? `The spec changed elsewhere (now v${snap.version}). Your unsaved edits are kept, and nothing is saved until you choose.`
            : 'The model holds no spec now (the engine may have restarted, or the spec was cleared). Your unsaved edits are kept, and nothing is saved until you choose.'}</span>
          {/* The newer spec is shown here, beside the words: Keep mine makes the next Save write over it, so it is on screen first (F11). */}
          <pre data-testid="spec-moved-theirs" className="sem-inset m-0 w-full whitespace-pre-wrap break-words" style={{ maxHeight: 160, overflow: 'auto', fontFamily: 'var(--sem-font-num)', fontSize: 'var(--sem-text-meta)' }}>{snap?.spec ? specText(snap.spec) : '(the model holds no spec now)'}</pre>
          <button onClick={() => setBaseContent(snapContent)} className="xe-link" data-testid="spec-moved-keep-mine" title="Keep what you typed. The next Save writes it over the spec shown above.">Keep mine</button>
          <button onClick={discard} className="xe-link">Load the server version</button>
        </div>
      )}
      {report && <BuildReport report={report} onClose={() => setReport(null)} />}
      {/* Words typed into a name box of a table, column or measure that was renamed or removed since: listed here with Keep mine / Discard, never held where nobody can see them. */}
      <MovedWords />
      <OrphanDrafts prefix="spec|buf|" live={draft ? specHoldIds(draft) : (snap ? [] : null)} what="box"
        nameOf={specHoldName}
        targetsFor={(_fields, id) => { if (id === NEW_TABLE_HOLD) return []; const kind = id.slice(0, id.indexOf(':') + 1) || 'model|', field = id.slice(id.lastIndexOf('|') + 1); return specHoldIds(draft).filter((x) => x !== id && x.startsWith(kind) && x.slice(x.lastIndexOf('|') + 1) === field).map((x) => ({ id: x, label: specHoldName(x) })); }} />

      <div className="flex-1 min-h-0 overflow-auto">
        {showChoices ? (
          <>
            {(dirty || editingJson) && <div className="pt-3 text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>Your draft is kept. Resume it to finish or discard your changes before choosing a new starting point.</div>}
            <fieldset disabled={dirty || editingJson || busy != null} className="border-0 p-0 m-0 min-w-0">
              <SpecWizard session={session} busy={busy} onAutogen={autogenModel} onSql={() => { setShowChoices(false); setShowFabric(true); }} onBlank={startBlank} onLoad={() => void loadFile()} />
            </fieldset>
          </>
        ) : editingJson ? (
          <div className="py-4 flex flex-col gap-2 h-full">
            {jsonGuard.moved && <EditMovedBanner what="spec" theirs={jsonGuard.theirs} onKeepMine={jsonGuard.keepMine} onUseNew={jsonGuard.useNew} disabled={busy != null} />}
            {bothHeld && !bothAcked && <div role="alert" data-testid="spec-both-held" className="sem-inset text-(length:--sem-text-label)" style={{ borderLeft: '3px solid var(--sem-warn)' }}>
              <div>You typed words here and you also edited the structured view. Both are kept and nothing was saved. Apply JSON writes these words and drops the structured edits.</div>
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <Btn onClick={() => setBothAcked(true)} testid="spec-both-keep-json">Keep the JSON words</Btn>
                <Btn onClick={() => { setJsonDraft(JSON.stringify(draft ?? {}, null, 2)); setBothAcked(true); }} testid="spec-both-use-structured">Use my structured edits</Btn>
              </div>
            </div>}
            <textarea value={jsonDraft} onChange={(e) => setJsonDraft(e.target.value)} spellCheck={false}
              className="flex-1 min-h-[calc(var(--sem-row-2)*6)] font-mono text-(length:--sem-text-meta) p-3 rounded"
              style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', resize: 'vertical' }} />
            <div className="flex items-center gap-2">
              <Btn primary onClick={applyJson} busy={busy === 'apply'}>Apply JSON</Btn>
              <Btn onClick={() => { setEditingJson(false); setJsonKept(false); }}>Cancel</Btn>
              <DraftMark show={jsonGuard.drafted} />
              <span style={{ color: 'var(--sem-muted)' }} className="text-(length:--sem-text-label)">Edit the spec as JSON, or let your assistant refine it through the shared connection. Changes appear here in the VS Code view, and your assistant sees them on its next call.</span>
            </div>
          </div>
        ) : !spec ? (
          // With the SQL step open there is one start on the page, not two: Back to start shows the choices again.
          showFabric ? null : <SpecWizard session={session} busy={busy} onAutogen={autogenModel} onSql={() => setShowFabric(true)} onBlank={startBlank} onLoad={() => void loadFile()} />
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
    <div className="pt-4 flex flex-col gap-4">
      {/* shared native-autocomplete sources for the format + display-folder inputs */}
      <datalist id="spec-formats">{FORMAT_PRESETS.filter((p) => p.value).map((p) => <option key={p.value} value={p.value}>{p.label}</option>)}</datalist>
      <datalist id="spec-folders">{folderSuggestions.map((f) => <option key={f} value={f} />)}</datalist>

      {/* Header */}
      <div className="flex items-baseline gap-3 flex-wrap">
        <NameInput hold="model|name" value={spec.name ?? ''} placeholder="Untitled model" ariaLabel="Model name"
          onCommit={(v) => edit((s) => { s.name = v; })}
          className="text-(length:--sem-text-page) font-bold" style={{ background: 'transparent', border: '1px solid transparent', borderRadius: 'var(--sem-radius-card)', padding: '1px var(--sem-space-1)', minWidth: 160 }} />
        <Chip>{spec.storageMode === 'directLake' ? 'Direct Lake' : 'Import'}</Chip>
        <Chip>CL {spec.compatibilityLevel ?? 1604}</Chip>
        {spec.source?.server && <span className="text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>{spec.source.server}{spec.source.database ? ' · ' + spec.source.database : ''}</span>}
        {counts && <span className="ml-auto text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>{counts.tables} tables ({counts.facts} fact · {counts.dims} dim) · {counts.relationships} relationships · {counts.measures} measures</span>}
      </div>

      {/* Tables */}
      <Section title={`Tables (${spec.tables?.length ?? 0})`} action={<TableAdder spec={spec} edit={edit} />}>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
          {(spec.tables ?? []).map((t, ti) => (
            <TableCard key={ti} t={t} ti={ti} spec={spec} edit={edit} daxModel={daxModel} folderSuggestions={folderSuggestions} />
          ))}
        </div>
        {(spec.tables?.length ?? 0) === 0 && <div className="text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>No tables yet. Add a table, or add a measure to a new measure group.</div>}
      </Section>

      {/* Orphaned measures (their table was removed/renamed) */}
      {orphanMeasures.length > 0 && (
        <Section title={`Unassigned measures (${orphanMeasures.length})`}>
          <div className="rounded p-3 flex flex-col gap-2" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-bad)' }}>
            <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-bad)' }}>These measures point at a table that no longer exists. Reassign a table or remove them.</div>
            {orphanMeasures.map(({ m, gi }) => (
              <div key={gi} className="flex items-center gap-2 text-(length:--sem-text-meta)">
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
          <div className="flex items-center gap-2 flex-wrap text-(length:--sem-text-meta)">
            {spec.dateTable && <Chip>{!spec.dateTable.name
              ? 'no date table marked'
              : spec.dateTable.dateColumn
                ? `date table: ${spec.dateTable.name}, marked on ${spec.dateTable.dateColumn}`
                : `date table: ${spec.dateTable.name}, added`}</Chip>}
            {spec.dateTable && spec.dateTable.note && <span style={{ color: 'var(--sem-muted)' }}>{spec.dateTable.note}</span>}
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
  const renameTable = (newName: string) => {
    // The person's own rename: words held on this table's other boxes (and its columns and measures) go with it, so the boxes that now stand for the new name show them.
    const finalName = newName && newName !== t.name ? uniqueName((spec.tables ?? []).filter((_, i) => i !== ti).map((x) => x.name), newName) : t.name;
    if (finalName !== t.name) carryHeldWords([[`table:${t.name}|`, `table:${finalName}|`], [`col:${t.name}/`, `col:${finalName}/`], [`measure:${t.name}/`, `measure:${finalName}/`]], `table:${t.name}|name`);
    renameTableInSpec(newName);
  };
  const renameTableInSpec = (newName: string) => edit((s) => {
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
    <div className="rounded p-4" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-2 mb-2 flex-wrap">
        <NameInput hold={`table:${t.name}|name`} value={t.name} ariaLabel="Table name" onCommit={renameTable}
          className="font-semibold" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', padding: '1px var(--sem-space-2)', minWidth: 120 }} />
        <span title="Star-schema role" style={{ display: 'inline-flex', width: 6, height: 6, borderRadius: '50%', background: ROLE_COLOR[t.role ?? 'isolated'] ?? 'var(--sem-muted)' }} />
        <Sel value={t.role ?? 'isolated'} ariaLabel="Table role" onChange={(v) => edit((s) => { s.tables[ti].role = v; })}
          options={TABLE_ROLES.map((r) => ({ value: r, label: r }))} />
        {measuresOnly && <Badge color="var(--sem-muted)">measures only</Badge>}
        <IconBtn title="Delete table" onClick={deleteTable}>✕</IconBtn>
      </div>

      {/* source binding (Direct Lake entity / schema / source) */}
      <div className="flex items-center gap-2 mb-2 flex-wrap text-(length:--sem-text-label)">
        <FieldMini hold={`table:${t.name}|entity`} label="entity" value={t.entity ?? ''} onCommit={(v) => edit((s) => { s.tables[ti].entity = v || undefined; })} />
        <FieldMini hold={`table:${t.name}|schema`} label="schema" value={t.schema ?? ''} onCommit={(v) => edit((s) => { s.tables[ti].schema = v || undefined; })} />
        <FieldMini hold={`table:${t.name}|source`} label="source" value={t.sourceName ?? ''} onCommit={(v) => edit((s) => { s.tables[ti].sourceName = v || undefined; })} />
      </div>

      {/* columns */}
      <div className="flex flex-col gap-1">
        {cols.map((c, ci) => (
          <ColumnRow key={ci} c={c} spec={spec} hold={`col:${t.name}/${c.name}`}
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
        {measuresOnly && <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{t.calculatedExpression ? 'calculated (columns inferred)' : 'no columns'}</span>}
        {!t.calculatedExpression && <button onClick={addColumn} className="self-start text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-accent)' }}>+ Column</button>}
      </div>

      {/* measures on this table */}
      <div className="mt-2 pt-2" style={{ borderTop: '1px solid var(--sem-border)' }}>
        {tableMeasures.map(({ m, gi }) => (
          <MeasureRow key={gi} m={m} daxModel={daxModel} hold={`measure:${t.name}/${m.name}`}
            onName={(v) => { if (v && v !== m.name) carryHeldWords([[`measure:${t.name}/${m.name}|`, `measure:${t.name}/${v}|`]], `measure:${t.name}/${m.name}|name`); edit((s) => { s.measures[gi].name = v; }); }}
            onDax={(v) => edit((s) => { s.measures[gi].dax = v; })}
            onFormat={(v) => edit((s) => { s.measures[gi].formatString = v || undefined; })}
            onFolder={(v) => edit((s) => { s.measures[gi].displayFolder = v || undefined; })}
            onDelete={() => edit((s) => { s.measures.splice(gi, 1); })} />
        ))}
        <button onClick={addMeasure} className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-accent)' }}>+ Measure</button>
      </div>
    </div>
  );
}

function ColumnRow({ hold, c, spec, onName, onType, onSummarize, onKey, onHidden, onDelete }: {
  hold: string; c: SpecColumn; spec: ModelSpec; onName: (v: string) => void; onType: (v: string) => void; onSummarize: (v: string) => void; onKey: (v: boolean) => void; onHidden: (v: boolean) => void; onDelete: () => void;
}) {
  void spec;
  const typeOptions = SPEC_DATA_TYPES.includes(c.dataType ?? '') || !c.dataType ? SPEC_DATA_TYPES : [c.dataType, ...SPEC_DATA_TYPES];
  return (
    <div className="flex items-center gap-2 text-(length:--sem-text-meta) flex-wrap">
      <NameInput hold={`${hold}|name`} value={c.name} ariaLabel="Column name" onCommit={onName}
        style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', padding: '1px var(--sem-space-2)', minWidth: 120, color: c.hidden ? 'var(--sem-muted)' : 'var(--sem-fg)' }} />
      <Sel value={c.dataType ?? 'String'} ariaLabel="Data type" onChange={onType} options={typeOptions.map((d) => ({ value: d, label: d }))} />
      <Sel value={c.summarizeBy ?? 'None'} ariaLabel="Default summarization" title="Default summarization" onChange={onSummarize} options={SUMMARIZE_BY.map((d) => ({ value: d, label: d === 'None' ? 'Σ none' : 'Σ ' + d }))} />
      <Toggle on={!!c.isKey} onToggle={onKey} title="Key column">key</Toggle>
      <Toggle on={!!c.hidden} onToggle={onHidden} title="Hidden">hidden</Toggle>
      <div className="ml-auto"><IconBtn title="Remove column" onClick={onDelete}>✕</IconBtn></div>
    </div>
  );
}

function MeasureRow({ hold, m, daxModel, onName, onDax, onFormat, onFolder, onDelete }: {
  hold: string; m: SpecMeasure; daxModel: DaxModel; onName: (v: string) => void; onDax: (v: string) => void; onFormat: (v: string) => void; onFolder: (v: string) => void; onDelete: () => void;
}) {
  return (
    <div className="flex flex-col gap-1 py-2" style={{ borderTop: '1px dashed var(--sem-border)' }}>
      <div className="flex items-center gap-2 flex-wrap text-(length:--sem-text-meta)">
        <NameInput hold={`${hold}|name`} value={m.name} ariaLabel="Measure name" onCommit={onName}
          className="font-medium" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', padding: '1px var(--sem-space-2)', minWidth: 140, color: 'var(--sem-accent)' }} />
        <label className="flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>format
          <NameInput hold={`${hold}|format`} value={m.formatString ?? ''} ariaLabel="Format string" list="spec-formats" placeholder="(default)" onCommit={onFormat}
            style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', padding: '1px var(--sem-space-2)', width: 110 }} /></label>
        <label className="flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>folder
          <NameInput hold={`${hold}|folder`} value={m.displayFolder ?? ''} ariaLabel="Display folder" list="spec-folders" placeholder="(none)" onCommit={onFolder}
            style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', padding: '1px var(--sem-space-2)', width: 140 }} /></label>
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
        <button onClick={() => setBuilding((v) => !v)} className="text-(length:--sem-text-label) px-2 py-1 rounded" style={{ color: 'var(--sem-accent)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>+ Relationship</button>
        <button onClick={detect} className="text-(length:--sem-text-label) px-2 py-1 rounded" style={{ color: 'var(--sem-fg)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>Detect relationships</button>
        <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Connect a column to a matching column in another table. The default is many rows linked to one lookup row.</span>
      </div>

      {building && <RelationshipBuilder spec={spec} onAdd={(r) => { edit((s) => { s.relationships = s.relationships ?? []; s.relationships.push(r); }); setBuilding(false); }} onCancel={() => setBuilding(false)} />}

      {proposals && (
        <div className="rounded p-4 flex flex-col gap-2" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-accent)' }}>
          <div className="flex items-center gap-2 text-(length:--sem-text-label)">
            <span className="font-semibold">{proposals.length > 0 ? `Detected ${proposals.length} proposal${proposals.length === 1 ? '' : 's'}` : 'No high-confidence relationships found'}</span>
            <span style={{ color: 'var(--sem-muted)' }}>by key-column name matching: review before accepting</span>
            <div className="ml-auto flex items-center gap-2">
              {proposals.length > 0 && <button onClick={acceptAll} className="text-(length:--sem-text-label) px-2 py-1 rounded" style={{ color: '#000', background: 'var(--sem-accent)' }}>Add all</button>}
              <IconBtn title="Dismiss" onClick={() => setProposals(null)}>✕</IconBtn>
            </div>
          </div>
          {proposals.map((r, i) => (
            <div key={i} className="flex items-center gap-2 text-(length:--sem-text-meta)">
              <RelText r={r} />
              <button onClick={() => accept(r)} className="ml-auto text-(length:--sem-text-label) px-2 py-1 rounded" style={{ color: 'var(--sem-accent)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>Add</button>
            </div>
          ))}
        </div>
      )}

      {rels.length === 0 && !building && <div className="text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>No relationships. Build one, or use Detect relationships (helpful right after a Fabric import).</div>}
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
    <div className="flex items-center gap-1 text-(length:--sem-text-meta) flex-wrap py-1">
      <Sel value={r.fromTable} ariaLabel="From table" onChange={(v) => onChange({ fromTable: v })} options={tables.map((n) => ({ value: n, label: n }))} />
      <Sel value={r.fromColumn} ariaLabel="From column" onChange={(v) => onChange({ fromColumn: v })} options={colsOf(r.fromTable).map((n) => ({ value: n, label: n }))} />
      <span style={{ color: 'var(--sem-accent)' }}>→</span>
      <Sel value={r.toTable} ariaLabel="To table" onChange={(v) => onChange({ toTable: v })} options={tables.map((n) => ({ value: n, label: n }))} />
      <Sel value={r.toColumn} ariaLabel="To column" onChange={(v) => onChange({ toColumn: v })} options={colsOf(r.toTable).map((n) => ({ value: n, label: n }))} />
      <Sel value={normCardinality(r.cardinality)} ariaLabel="Cardinality" title="Relationship cardinality (from → to)" onChange={(v) => onChange({ cardinality: normCardinality(v) })} options={CARDINALITY} />
      <Sel value={r.crossFilter ?? 'OneDirection'} ariaLabel="Cross-filter" title="Cross-filter direction" onChange={(v) => onChange({ crossFilter: v })} options={CROSSFILTER} />
      <Toggle on={r.isActive !== false} onToggle={(v) => onChange({ isActive: v })} title="Active relationship">active</Toggle>
      {(!fromOk || !toOk) && <span className="text-(length:--sem-text-label) xe-dot xe-dot-bad" title="A table or column used by this relationship is missing from the draft">unresolved</span>}
      <div className="ml-auto"><IconBtn title="Remove relationship" onClick={onDelete}>✕</IconBtn></div>
      {r.note && r.isActive === false && <div className="basis-full text-(length:--sem-text-label)" data-testid="spec-rel-note" style={{ color: 'var(--sem-muted)' }}>{r.note}</div>}
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
    <div className="rounded p-4 flex flex-col gap-2" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-2 flex-wrap text-(length:--sem-text-meta)">
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
          className="text-(length:--sem-text-label) px-2 py-1 rounded disabled:opacity-50" style={{ color: '#000', background: 'var(--sem-accent)' }}>Add relationship</button>
        <button onClick={onCancel} className="text-(length:--sem-text-label) px-2 py-1 rounded" style={{ color: 'var(--sem-fg)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>Cancel</button>
        {dup && <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-warn)' }}>That relationship already exists.</span>}
        {fromTable === toTable && <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-warn)' }}>Pick two different tables.</span>}
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
  // A typed table name is held (sweep 3, site 2): when the assistant clears the spec the wizard replaces this box, and the words are listed with the other words that
  // have no box; when a spec is back the box opens on them. Add and Cancel (the person's presses) end them.
  const scope = `spec|buf|${NEW_TABLE_HOLD}`;
  const held = useRef(heldDraft<string>(scope)).current;
  const [open, setOpenState] = useState(!!held);
  const [name, setNameState] = useState(held?.value ?? '');
  const [role, setRole] = useState('dimension');
  const setName = (text: string) => { setNameState(text); const m = draftModel(); if (m) draftStore.set(m, scope, '', { value: text, base: '', label: 'The new table name' }); };
  const setOpen = (o: boolean) => { setOpenState(o); if (!o) { setNameState(''); const m = draftModel(); if (m) draftStore.clear(m, scope, ''); } };
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
    setRole('dimension'); setOpen(false);
  };
  if (!open) return <button onClick={() => setOpen(true)} className="text-(length:--sem-text-label) px-2 py-1 rounded" style={{ color: 'var(--sem-accent)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>+ Table</button>;
  return (
    <span className="inline-flex items-center gap-2">
      <input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="table name…"
        onKeyDown={(e) => { if (e.key === 'Enter') add(); else if (e.key === 'Escape') setOpen(false); }}
        className="text-(length:--sem-text-meta) px-2 py-1 rounded outline-none" style={{ width: 150, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      <Sel value={role} ariaLabel="New table role" onChange={setRole}
        options={[...TABLE_ROLES.map((r) => ({ value: r, label: r })), { value: '__measuregroup__', label: 'Measure group' }]} />
      <button onClick={add} className="text-(length:--sem-text-label) px-2 py-1 rounded" style={{ color: '#000', background: 'var(--sem-accent)' }}>Add</button>
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
    <div className="px-4 py-2 border-b text-(length:--sem-text-meta)" style={{ borderColor: 'var(--sem-border)', background: 'color-mix(in srgb,var(--sem-' + (tone === 'good' ? 'good' : 'warn') + ') 10%, transparent)' }}>
      <div className="flex items-center gap-3 flex-wrap">
        <span className="font-semibold">Built:</span>
        <span><b className="tnum">{report.created.length}</b> created</span>
        <span style={{ color: 'var(--sem-muted)' }}><b className="tnum">{report.skipped.length}</b> skipped</span>
        {report.errors.length > 0 && <span style={{ color: 'var(--sem-bad)' }}><b className="tnum">{report.errors.length}</b> errors</span>}
        <span style={{ color: 'var(--sem-muted)' }}>tables {report.tablesBefore}→{report.tablesAfter} · measures {report.measuresBefore}→{report.measuresAfter}</span>
        <button onClick={onClose} className="ml-auto" style={{ color: 'var(--sem-muted)' }}>✕</button>
      </div>
      {report.errors.length > 0 && <div className="mt-1" style={{ color: 'var(--sem-bad)' }}>{report.errors.slice(0, 5).map((e, i) => <div key={i}>· {e}</div>)}</div>}
      {(report.notes?.length ?? 0) > 0 && <div className="mt-1" data-testid="spec-build-notes" style={{ color: 'var(--sem-muted)' }}>{report.notes!.map((n, i) => <div key={i}>{n}</div>)}</div>}
    </div>
  );
}

/** What the draft call sends for a saved endpoint, positionally: server, database, sign-in, storage, origin, and the
 *  tenant LAST and only when one is set (the same skew rule autogenFabric follows). Exported so the test runs it. */
export function sqlDraftArgs(r: Pick<SqlSourceRecord, 'server' | 'database' | 'authMode' | 'tenantId'>, storageMode: string): unknown[] {
  const args: unknown[] = [(r.server ?? '').trim(), (r.database ?? '').trim(), (r.authMode ?? '').trim() || 'azcli', storageMode, 'human'];
  const tenant = (r.tenantId ?? '').trim();
  if (tenant) args.push(tenant);
  return args;
}

/** The saved endpoints as the SQL step shows them: null while reading, 'failed' when the engine did not answer. A
 *  failed read is NOT an empty list: drawing it as "none saved" told the person something untrue (verifier M3). */
export type SavedSqlSources = SqlSourceRecord[] | null | 'failed';
export function readSavedSqlSources(): Promise<SqlSourceRecord[] | 'failed'> {
  return listSqlSources().then((r) => r ?? [], () => 'failed' as const);
}

// One name for each way of signing in, the ones Connections shows for the same saved endpoint (copy.ts SIGN_IN_MODES).
const SIGN_IN_WORDS: Record<string, string> = Object.fromEntries(SIGN_IN_MODES.map((m) => [m.value, m.label]));
const SELECT_STYLE: React.CSSProperties = { background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', padding: 'var(--sem-space-1) var(--sem-space-2)' };

/**
 * The wizard's SQL step. The SQL endpoints saved on Connections come FIRST (Kane, 28 Sep: "we already add a sql
 * endpoint to connections. This is what would be used"): the same record and the same Entra sign-in, and no password
 * anywhere. Typing an endpoint stays, as the fallback, and is what shows when none is saved yet.
 */
export function SqlSourceStep({ sources, busy, onDraft, onAddOnConnections, onRetry, typed, onTyped, onDraftTyped }: {
  sources: SavedSqlSources; busy: boolean;
  onDraft: (r: SqlSourceRecord, storageMode: string) => void; onAddOnConnections: () => void; onRetry?: () => void;
  typed?: { server: string; database: string; storageMode: string; authMode: string; tenant: string };
  onTyped?: (patch: Partial<{ server: string; database: string; storageMode: string; authMode: string; tenant: string }>) => void;
  onDraftTyped?: () => void;
}) {
  const [typing, setTyping] = useState(false);
  const [picked, setPicked] = useState<string>('');
  const [storage, setStorage] = useState(typed?.storageMode ?? 'import');
  const list = Array.isArray(sources) ? sources : [];
  const chosen = list.find((r) => r.id === picked) ?? list[0];
  const storageSelect = (value: string, onChange: (v: string) => void) => (
    <label className="flex flex-col gap-1">
      <span style={{ color: 'var(--sem-muted)' }}>Storage</span>
      <select value={value} onChange={(e) => onChange(e.target.value)} style={SELECT_STYLE}>
        <option value="import">Import</option>
        <option value="directLake">Direct Lake</option>
      </select>
    </label>
  );
  const frame = (children: React.ReactNode) => (
    <div className="flex flex-col gap-2 py-2 border-b text-(length:--sem-text-meta)" data-testid="spec-sql-step" style={{ borderColor: 'var(--sem-border)' }}>{children}</div>
  );
  if (sources === null) return frame(<span style={{ color: 'var(--sem-muted)' }}>Reading your SQL endpoints from Connections...</span>);

  if (list.length > 0 && !typing) return frame(<>
    <div className="font-semibold">Use a SQL endpoint from Connections</div>
    <span style={{ color: 'var(--sem-muted)' }}>It signs in the way Connections does. No password is kept.</span>
    <div role="radiogroup" aria-label="SQL endpoints from Connections" className="flex flex-col gap-3">
      {list.map((r) => (
        <label key={r.id} className="sem-card flex items-center gap-2 cursor-pointer" data-testid="spec-sql-saved"
          style={{ borderColor: chosen?.id === r.id ? 'var(--sem-strong-border)' : undefined }}>
          <input type="radio" name="spec-sql-saved" checked={chosen?.id === r.id} onChange={() => setPicked(r.id)} />
          <span className="flex flex-col min-w-0">
            <b className="truncate">{r.name}</b>
            <span className="truncate" style={{ color: 'var(--sem-muted)' }}>{r.server}, {r.database}{r.authMode ? `, ${SIGN_IN_WORDS[r.authMode] ?? r.authMode}` : ''}</span>
          </span>
        </label>
      ))}
    </div>
    <div className="flex items-end gap-2 flex-wrap">
      {storageSelect(storage, setStorage)}
      <Btn primary onClick={() => { if (chosen) onDraft(chosen, storage); }} busy={busy} disabled={!chosen}>Read the tables</Btn>
      <button type="button" className="xe-link" data-testid="spec-sql-type" onClick={() => setTyping(true)}>Type an endpoint instead</button>
    </div>
    <span style={{ color: 'var(--sem-muted)' }}>Reads table and column details only. Nothing is copied into the model until you review the draft and choose Build into model.</span>
  </>);

  const t = typed ?? { server: '', database: '', storageMode: 'import', authMode: 'azcli', tenant: '' };
  const set = onTyped ?? (() => {});
  return frame(<>
    <div className="flex items-end gap-2 flex-wrap">
      <Field label="SQL endpoint" value={t.server} onChange={(v) => set({ server: v })} placeholder="server.example.com" wide />
      <Field label="Database" value={t.database} onChange={(v) => set({ database: v })} placeholder="Source database" />
      {storageSelect(t.storageMode, (v) => set({ storageMode: v }))}
      <label className="flex flex-col gap-1">
        <span style={{ color: 'var(--sem-muted)' }}>Sign-in</span>
        <select value={t.authMode} onChange={(e) => set({ authMode: e.target.value })} style={SELECT_STYLE}>
          {SIGN_IN_MODES.filter((m) => m.value !== 'serviceprincipal').map((m) => <option key={m.value} value={m.value}>{m.label}</option>)}
        </select>
      </label>
      <Field label="Tenant (optional)" value={t.tenant} onChange={(v) => set({ tenant: v })} placeholder="Tenant id or domain, e.g. contoso.com (blank = your az default)" wide />
      <Btn primary onClick={() => onDraftTyped?.()} busy={busy}>Read the tables</Btn>
    </div>
    <span style={{ color: 'var(--sem-muted)' }}>Reads table and column details only. Nothing is copied into the model until you review the draft and choose Build into model.</span>
    {sources === 'failed'
      ? <span style={{ color: 'var(--sem-muted)' }} data-testid="spec-sql-read-failed">The SQL endpoints saved on Connections could not be read just now, so none are listed. Type one here, or{' '}
          <button type="button" className="xe-link" data-testid="spec-sql-retry" onClick={onRetry}>try again</button>.</span>
      : list.length > 0
      ? <button type="button" className="xe-link self-start" onClick={() => setTyping(false)}>Use a SQL endpoint from Connections</button>
      : <span style={{ color: 'var(--sem-muted)' }}>No SQL endpoint is saved on Connections yet. Save one there once and the draft uses its sign-in, with no password kept.{' '}
          <button type="button" className="xe-link" data-testid="spec-sql-add" onClick={onAddOnConnections}>Add one on Connections</button></span>}
  </>);
}

function SpecWizard({ session, busy, onAutogen, onSql, onBlank, onLoad }: {
  session?: { modelName?: string; tables?: number; measures?: number } | null;
  busy: string | null; onAutogen: () => void; onSql: () => void; onBlank: () => void; onLoad: () => void;
}) {
  const hasObjects = (session?.tables ?? 0) > 0 || (session?.measures ?? 0) > 0;
  return (
    <div className="pt-4">
      <div className="sem-grid">
        <div className="sem-label">{hasObjects ? 'Model Spec wizard' : 'New model wizard'}, step 1</div>
        <div className="sem-page-title">Create the first draft</div>
        <div className="max-w-[680px] text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>
          A Model Spec is a plan for your tables, relationships and calculations. Choose where to start, review the draft here, then select Build into model when you are ready.
        </div>
        {hasObjects && <WizardChoice title={`Draft from ${session?.modelName || 'the open model'}`} detail="Read the current model into a first draft. The model is not changed." onClick={onAutogen} busy={busy === 'model'} primary />}
        <WizardChoice title="Start from scratch" detail="Begin with an empty structure and add tables, relationships and measures during review." onClick={onBlank} busy={busy === 'blank'} primary={!hasObjects} />
        <WizardChoice title="Draft from a SQL source" detail="Read the tables, columns and keys from a Fabric SQL endpoint with a Microsoft sign-in. No data rows are copied and no password is kept." onClick={onSql} />
        <WizardChoice title="Open a saved Model Spec" detail="Continue a model plan previously saved as a JSON file." onClick={onLoad} busy={busy === 'load'} />
        <div className="border-t pt-4 text-(length:--sem-text-meta)" style={{ borderColor: 'var(--sem-rule)', color: 'var(--sem-muted)' }}>
          <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>Step 2: Review and build.</span> Nothing is published by this wizard. Build is one undoable model change; publishing remains a separate reviewed action.
        </div>
      </div>
    </div>
  );
}

function WizardChoice({ title, detail, onClick, busy, primary }: { title: string; detail: string; onClick: () => void; busy?: boolean; primary?: boolean }) {
  return (
    <button onClick={onClick} disabled={busy} className="sem-card sem-span-6 text-left disabled:opacity-50"
      style={{ borderColor: primary ? 'var(--sem-strong-border)' : undefined, color: 'var(--sem-fg)' }}>
      <div className="sem-card-title">{busy ? 'Working…' : title}</div>
      <div className="mt-1 text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>{detail}</div>
    </button>
  );
}

// ---- little styled atoms (match the other tabs' --sem-* + Tailwind conventions) ----
function Section({ title, action, children }: { title: string; action?: React.ReactNode; children: React.ReactNode }) {
  return (
    <div>
      <div className="flex items-center gap-2 mb-2">
        <div className="text-(length:--sem-text-label) font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>{title}</div>
        {action && <div className="ml-auto">{action}</div>}
      </div>
      {children}
    </div>
  );
}
// Every box the structured view holds typed words for, as the ids its NameInput `hold` props use (`table:Sales|name`, `col:Sales/Amount|name`, `measure:Sales/Total|format`, `model|name`).
// Words held under an id that is not in this list belong to a table, column or measure that was renamed or removed: they are listed (OrphanDrafts), never held where nobody can see them.
export function specHoldIds(spec: ModelSpec | null | undefined): string[] {
  if (!spec) return [];
  const ids = ['model|name', NEW_TABLE_HOLD];
  for (const t of spec.tables ?? []) {
    for (const f of ['name', 'entity', 'schema', 'source']) ids.push(`table:${t.name}|${f}`);
    for (const c of t.columns ?? []) ids.push(`col:${t.name}/${c.name}|name`);
  }
  for (const m of spec.measures ?? []) for (const f of ['name', 'format', 'folder']) ids.push(`measure:${m.table}/${m.name}|${f}`);
  return ids;
}
/** Move the words held under one object's boxes to the boxes of the name it was given (the person's own rename). `pairs` are id prefixes, old then new; `except` is the name box
 *  being committed. A target that already holds words keeps them: nothing moves over them. */
function carryHeldWords(pairs: Array<[string, string]>, except: string): void {
  const m = draftModel();
  if (!m) return;
  for (const e of draftStore.scopes(m, 'spec|buf|')) {
    const id = e.scope.slice('spec|buf|'.length);
    if (id === except) continue;   // the name box being committed: its words are the rename itself, and the box ends them
    const p = pairs.find(([from]) => id.startsWith(from));
    if (p) draftStore.moveScope(m, e.scope, 'spec|buf|' + p[1] + id.slice(p[0].length));
  }
}
/** The "+ Table" box's words, held as a box of the spec page (it is not on any table). */
const NEW_TABLE_HOLD = 'new-table|name';
/** What a person calls the box a spec buffer was typed in. */
export function specHoldName(id: string): string {
  if (id === NEW_TABLE_HOLD) return 'The new table name';
  const [what, field = 'name'] = id.split('|');
  const named = what === 'model' ? 'The model' : what.replace(/^table:/, 'Table ').replace(/^col:/, 'Column ').replace(/^measure:/, 'Measure ').replace('/', ' / ');
  return `${named}, ${field}`;
}
// Words typed into a box whose live value moved underneath them (join 11 solui, U2, rule R-B: a draft ends only when the engine accepted exactly that text or the person discarded it).
// Each such box lists itself here and MovedWords shows the list, because the box itself must stay a bare <input>. Keep mine writes the words, Use the new one drops them.
type MovedEntry = { label: string; live: string; mine: string; keepMine: () => void; useLive: () => void };
const movedWords = new Map<string, MovedEntry>();
const movedListeners = new Set<() => void>();
let movedSeq = 0;
function publishMoved(key: string, entry: MovedEntry | null): void {
  if (entry) movedWords.set(key, entry); else if (!movedWords.delete(key)) return;
  movedListeners.forEach((f) => f());
}
function MovedWords() {
  const [, tick] = useState(0);
  useEffect(() => { const f = () => tick((n) => n + 1); movedListeners.add(f); f(); return () => { movedListeners.delete(f); }; }, []);
  const entries = [...movedWords.entries()];
  if (!entries.length) return null;
  const shown = (t: string) => t === '' ? '(empty)' : `\u201c${t}\u201d`;
  return (
    <div role="alert" data-testid="spec-words-moved" className="sem-inset text-(length:--sem-text-label) flex flex-col gap-2" style={{ borderLeft: '3px solid var(--sem-warn)' }}>
      {entries.map(([key, e]) => (
        <div key={key} className="flex flex-wrap items-center gap-2">
          <span>{e.label} changed to {shown(e.live)} while you were typing. Your words are kept: {shown(e.mine)}.</span>
          <button type="button" className="xe-link" data-testid="spec-words-keep-mine" onClick={e.keepMine}>Keep mine</button>
          <button type="button" className="xe-link" data-testid="spec-words-use-new" onClick={e.useLive}>Use the new one</button>
        </div>
      ))}
    </div>
  );
}
// A commit-on-blur text input: holds its own buffer while typing (so cursor/focus never jump on the parent re-render)
// and only fires onCommit on blur/Enter. Escape reverts. Optional `list` wires a native <datalist> for suggestions.
function NameInput({ hold, value, onCommit, placeholder, className, style, ariaLabel, list }: {
  /** Which object and field this is (a table's name, a measure's folder), so the typed buffer is held by model and object on every change (F9, drafts-sol). Without it nothing is held. */
  hold?: string;
  value: string; onCommit: (v: string) => void; placeholder?: string; className?: string; style?: React.CSSProperties; ariaLabel?: string; list?: string;
}) {
  // The words in this box are the person's before they are an edit: a blur or Enter makes them one, but an engine that goes away replaces the editor with no blur at all.
  // So each change is held in the shared draft store (by the model and this object), and the box opens on what was held. A commit, Escape or typing it back ends the hold.
  // A live value that moves while the box holds typed words never replaces them (R-B): the box keeps the words, shows the newer value in MovedWords, and a blur keeps the
  // hold too, because a blur is not a decision between the two. Keep mine, Use the new one, Escape, or the live value becoming exactly these words, ends it.
  const scope = hold ? `spec|buf|${hold}` : '';
  const held = useRef(scope ? heldDraft<string>(scope) : undefined).current;
  const [v, setV] = useState(held ? held.value : value);
  const typedOn = useRef<string | null>(held ? held.base : null);   // the live value the buffer was typed against; null = nothing typed
  // The object the words in this box belong to. The rows are drawn by position, so when the assistant removes or reorders tables, columns or measures React hands
  // this box ANOTHER object (sweep 3, site 1). The words never go with the box: they stay held under their own object, and the box shows its new object's own words or value.
  const own = useRef(scope);
  const boxKey = useRef(`box:${++movedSeq}`).current;
  const key = scope || boxKey;
  const reverting = useRef(false);   // Escape sets this so the ensuing blur reverts instead of committing (setV is async, so onBlur can't just read v)
  const first = useRef(true);
  const moved = typedOn.current !== null && v !== value && value !== typedOn.current;
  const [, redraw] = useState(0);   // the hold lives in a ref, so ending it must draw the box again (the moved note and its outline go)
  const end = () => { const m = draftModel(); if (own.current && m) draftStore.clear(m, own.current, ''); typedOn.current = null; redraw((n) => n + 1); };
  const keep = (text: string) => {
    if (text === value) { end(); return; }
    const m = draftModel(); if (!own.current || !m) return;
    draftStore.set(m, own.current, '', { value: text, base: typedOn.current ?? value });
  };
  // Show the words held for this box's object, read now: on a new object, or when words are put on it from the list of words with no box (Keep mine on it).
  const adopt = (): boolean => {
    const h = own.current ? heldDraft<string>(own.current) : undefined;
    if (!h) return false;
    if (h.value === value) { end(); setV(value); return true; }   // the object already holds exactly these words: nothing is left to keep
    typedOn.current = h.base; setV(h.value); redraw((n) => n + 1);
    return true;
  };
  const hasHeld = useHasDraft(scope);
  useEffect(() => {
    if (first.current) { first.current = false; if (held) return; }
    if (own.current !== scope) {
      own.current = scope;
      if (adopt()) return;
      typedOn.current = null; setV(value); redraw((n) => n + 1);   // the last object's words stay held under it (listed when it is gone)
      return;
    }
    if (typedOn.current !== null && v !== value) return;   // typed words stay; the newer value is shown (moved)
    if (typedOn.current !== null) end();                   // the live value became exactly these words: the draft is done
    setV(value);
  }, [value, scope]);
  useEffect(() => { if (hasHeld && typedOn.current === null && own.current === scope) adopt(); }, [hasHeld]);   // eslint-disable-line react-hooks/exhaustive-deps
  const change = (text: string) => { if (typedOn.current === null) typedOn.current = value; setV(text); keep(text); };
  const useLive = () => { setV(value); end(); };
  const keepMine = () => { onCommit(v); end(); };
  const label = hold === 'model|name' ? 'The model name' : hold ? specHoldName(hold) : (ariaLabel ?? 'A box');
  useEffect(() => {
    if (!moved) { publishMoved(key, null); return; }
    publishMoved(key, { label, live: value, mine: v, keepMine, useLive });
    return () => publishMoved(key, null);
  }, [moved, v, value, key]);
  const commit = () => {
    if (reverting.current) { reverting.current = false; useLive(); return; }
    if (moved) return;   // not a decision: the words stay in the box and held until Keep mine or Use the new one
    if (v !== value) { onCommit(v); }
    end();
  };
  return (
    <input value={v} onChange={(e) => change(e.target.value)} onBlur={commit} placeholder={placeholder} aria-label={ariaLabel} list={list}
      onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); else if (e.key === 'Escape') { reverting.current = true; (e.target as HTMLInputElement).blur(); } }}
      className={'outline-none ' + (className ?? '')} style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontSize: 'var(--sem-text-meta)', ...style, ...(moved ? { boxShadow: 'inset 0 0 0 1px var(--sem-warn)' } : {}) }} />
  );
}
function FieldMini({ hold, label, value, onCommit }: { hold?: string; label: string; value: string; onCommit: (v: string) => void }) {
  return (
    <label className="inline-flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>{label}
      <NameInput hold={hold} value={value} ariaLabel={label} placeholder="Optional" onCommit={onCommit}
        style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', padding: '0 var(--sem-space-1)', width: 96, color: 'var(--sem-fg)' }} />
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
      style={{ background: 'var(--sem-surface)', color: missing ? 'var(--sem-bad)' : 'var(--sem-fg)', border: '1px solid ' + (missing ? 'var(--sem-bad)' : 'var(--sem-border)'), borderRadius: 'var(--sem-radius-card)', padding: '1px var(--sem-space-1)', fontSize: 'var(--sem-text-meta)' }}>
      {missing && <option value={value} disabled>{value}</option>}
      {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
    </select>
  );
}
function Toggle({ on, onToggle, title, children }: { on: boolean; onToggle: (v: boolean) => void; title?: string; children: React.ReactNode }) {
  return (
    <button onClick={() => onToggle(!on)} title={title} className="text-(length:--sem-text-label) px-2 py-1 rounded font-medium"
      style={on ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>{children}</button>
  );
}
function IconBtn({ onClick, title, children }: { onClick: () => void; title?: string; children: React.ReactNode }) {
  return (
    <button onClick={onClick} title={title} className="sem-btn sem-btn-sm">{children}</button>
  );
}
function Btn({ children, onClick, primary, busy, disabled, title, testid }: { children: React.ReactNode; onClick: () => void; primary?: boolean; busy?: boolean; disabled?: boolean; title?: string; testid?: string }) {
  return (
    <button onClick={onClick} disabled={busy || disabled} title={title} data-testid={testid}
      className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>
      {busy ? '…' : children}
    </button>
  );
}
function Chip({ children }: { children: React.ReactNode }) {
  return <span className="text-(length:--sem-text-label) px-2 py-1 rounded-full" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{children}</span>;
}
function Badge({ color, children }: { color: string; children: React.ReactNode }) {
  return <span className="text-(length:--sem-text-label) px-1 rounded align-middle" style={{ background: color, color: '#000' }}>{children}</span>;
}
function Banner({ tone, children }: { tone: 'bad' | 'good' | 'warn'; children: React.ReactNode }) {
  const c = tone === 'bad' ? 'var(--sem-bad)' : tone === 'warn' ? 'var(--sem-warn)' : 'var(--sem-good)';
  return <div className="my-2 xe-msg" style={{ '--xe-dot': c } as React.CSSProperties}>{children}</div>;
}
function Field({ label, value, onChange, placeholder, wide }: { label: string; value: string; onChange: (v: string) => void; placeholder?: string; wide?: boolean }) {
  return (
    <label className="flex flex-col gap-1">
      <span style={{ color: 'var(--sem-muted)' }}>{label}</span>
      <input value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder}
        style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', padding: 'var(--sem-space-1) var(--sem-space-2)', minWidth: wide ? 320 : 160 }} />
    </label>
  );
}
