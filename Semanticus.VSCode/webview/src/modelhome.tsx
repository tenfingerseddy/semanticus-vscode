import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { focusSelectInProperties, rpc, runHostCommand, selectInProperties } from './bridge';
import { useConnection } from './connection';
import type { ModelGraph } from './diagram';
import { VPAQ_COMPONENT_COLORS, VPAQ_UNATTRIBUTED_COLOR, type VpaqColumn, type VpaqTable } from './echart';
import { publishPermissionDescription, type AgentPolicy } from './permissions';
import { publishStageName } from './publishcopy';
import { objectLabel, objectToRef, type ObjectRef } from './route';
import { ProBadge, useFeature, PENDING_ACCESS, type FeatureGrant } from './pro';
import {
  bandGeometry, changesCard, checksCard, formatBytes, formatCount, heroClauses, instructionCount, memoryBand, tableRows,
  type ChecksHistory, type ChecksRun, type OverviewRow, type TableKind,
} from './overviewfacts.mjs';

// The Model Overview: the landing page for a model. Its job is to answer "what am I looking at" before anything is
// clicked, so every panel is a real measurement of the open model and nothing is a placeholder. A fact the engine
// has not given us yet produces a DIFFERENT panel (an empty state, a fallback band, a missing tile), never a zero
// dressed up as a reading.
//
// Everything except getModelGraph is best effort: each card owns its own loading, error and empty state, so one
// engine call that fails or is gated leaves the rest of the page intact.

/** One suggested addition to the model Primer, waiting for a person. The three operations behind these
 *  (list, accept, reject) stay FREE: the Primer itself is a Pro Model notes page now, but a suggestion is a
 *  human decision, and taking that decision away with the page would break the two door promise. */
interface PrimerSuggestion {
  id: string; section: string; markdown: string; capturedUtc?: string | null; provenance?: string;
}
interface PrimerSuggestionList { suggestions?: PrimerSuggestion[]; note?: string | null }

type LoadState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'ready'; graph: ModelGraph };

type Remote<T> =
  | { status: 'loading' }
  /** The card decided not to ask. Distinct from loading, which would sit on a spinner for ever, and from
   *  error, which would claim something went wrong. */
  | { status: 'off' }
  | { status: 'error'; message: string }
  | { status: 'ready'; value: T };

interface AiInstructions { present: boolean; instructions?: string | null; length: number; limit: number; culture?: string | null; note?: string | null }
interface ReadinessCard { grade?: string; overall?: number }

/** The Size by table page's scan, handed down from the holder that owns it. Overview never starts a scan by
 *  itself: landing on a page must not fire a DMV sweep at a live model. It reuses the last one, and the
 *  Scan sizes button runs exactly the scan Size by table runs. */
export interface OverviewStorage {
  scanState: { report: { modelSize: number; tables: VpaqTable[]; topColumns: VpaqColumn[]; storageMode?: string; caveat?: string }; at: number } | null;
  busy: boolean;
  err: string | null;
  scan: () => Promise<void>;
}

const KIND_COLOR: Record<TableKind, string> = {
  // The same four colours the Diagram paints a table with, so a kind is one colour everywhere in Studio.
  data: 'var(--sem-kind-data)', date: 'var(--sem-kind-date)',
  calculated: 'var(--sem-kind-calc)', fieldParameter: 'var(--sem-kind-fp)',
};
const KIND_TEXT: Record<TableKind, string> = {
  data: 'Data table', date: 'Date table', calculated: 'Calculated', fieldParameter: 'Field parameter',
};
const PART_COLOR: Record<string, string> = {
  data: VPAQ_COMPONENT_COLORS.data, dict: VPAQ_COMPONENT_COLORS.dict, hash: VPAQ_COMPONENT_COLORS.hash,
  rest: VPAQ_UNATTRIBUTED_COLOR, columns: 'var(--sem-accent)',
};
const PART_TEXT: Record<string, string> = {
  data: 'Data', dict: 'Dictionary', hash: 'Hash indexes', rest: 'Not broken down by the scan', columns: 'Share of the model by columns',
};

/** The most edits App.tsx's activity list keeps (its own slice(0, 200)). At the cap the count is a floor, not
 *  a total, and the Changes card says so rather than printing 200 as though it were the whole story. */
const ACTIVITY_CAP = 200;

/** What a compatibility level actually blocks, named one capability at a time.
 *
 *  A level below a floor is NOT the same as "your model is held back". The first version of the prompt below
 *  treated every level under 1701 as an active blocker and said date calculations needed it, and both halves
 *  were too broad: `advmodels.tsx:709` explicitly offers the classic date-table approach below 1701, so date
 *  calculations work perfectly well down there. The one thing that is genuinely refused is AUTHORING A
 *  CALENDAR. So the prompt names that capability, and only that capability.
 *
 *  A list rather than a constant because the next floor that matters will be a second row here, and a second
 *  row must not be able to widen the sentence the first one prints. */
const LEVEL_BLOCKS: { capability: string; floor: number }[] = [
  { capability: 'Calendars', floor: 1701 },
];
/** The first capability this level actually blocks, or null when it blocks none of them. Null is also what
 *  an unknown level gives, because "not known to be blocking" is not "blocking". */
function blockedCapability(level?: number): { capability: string; floor: number } | null {
  if (typeof level !== 'number' || level <= 0) return null;
  return LEVEL_BLOCKS.find((entry) => level < entry.floor) ?? null;
}

function messageOf(error: unknown) { return error instanceof Error ? error.message : String(error); }

/** One best-effort engine read with its own state. The page never waits on it, and a failure stays inside
 *  the card that asked for it. */
function useRemote<T>(load: () => Promise<T>, keys: unknown[], skip?: boolean): [Remote<T>, () => void] {
  const [state, setState] = useState<Remote<T>>({ status: 'loading' });
  const [retry, setRetry] = useState(0);
  const generation = useRef(0);
  const loadRef = useRef(load);
  loadRef.current = load;
  useEffect(() => {
    const mine = ++generation.current;
    // SUPPRESSED, not filtered. A card that belongs to a feature this plan does not reach must not make the
    // call at all: hiding the answer still spends the round trip and still asks the engine for paid data.
    if (skip) { setState({ status: 'off' }); return; }
    setState({ status: 'loading' });
    loadRef.current().then(
      (value) => { if (mine === generation.current) setState({ status: 'ready', value }); },
      (error: unknown) => { if (mine === generation.current) setState({ status: 'error', message: messageOf(error) }); },
    );
    return () => { if (generation.current === mine) generation.current++; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...keys, retry, skip]);
  return [state, useCallback(() => setRetry((n) => n + 1), [])];
}

export function ModelHome({ sessionId, revision, changeNonce, modelName, compatibilityLevel, selected, sessionEdits, agentPolicy, storage, onGo, onSelect, onConnections }: {
  sessionId: string; revision?: number; changeNonce: number; modelName?: string;
  /** Absent on an engine that does not report it. Absent means "not known to be blocking", so the action
   *  below stays hidden rather than nagging about a number nobody measured. */
  compatibilityLevel?: number; selected?: ObjectRef;
  /** Edits this webview has SEEN since the model opened, capped by App's own activity list. It is not a
   *  destination comparison, and nothing on this page may describe it as one. */
  sessionEdits: number;
  agentPolicy: AgentPolicy | null | undefined;
  storage: OverviewStorage | null;
  onGo: (tool: string, target?: string, addTables?: string[]) => void;
  onSelect: (object: ObjectRef) => void;
  onConnections: () => void;
}) {
  const [load, setLoad] = useState<LoadState>({ status: 'loading' });
  const [retry, setRetry] = useState(0);
  const generation = useRef(0);
  useEffect(() => {
    const mine = ++generation.current;
    setLoad({ status: 'loading' });
    rpc<ModelGraph>('getModelGraph').then(
      (graph) => { if (mine === generation.current) setLoad({ status: 'ready', graph }); },
      (error: unknown) => { if (mine === generation.current) setLoad({ status: 'error', message: messageOf(error) }); },
    );
    return () => { if (generation.current === mine) generation.current++; };
  }, [sessionId, revision, changeNonce, retry]);

  const keys = useMemo(() => [sessionId, revision, changeNonce], [sessionId, revision, changeNonce]);
  const [instructions, reloadInstructions] = useRemote<AiInstructions>(() => rpc<AiInstructions>('getAiInstructions'), keys);
  const [readiness] = useRemote<ReadinessCard>(() => rpc<ReadinessCard>('aiReadinessScan'), keys);
  // Tests is one whole Pro feature, so unless this plan GRANTS it the card asks the engine nothing at all.
  //
  // `=== 'granted'`, never `!== 'denied'`. An entitlement that has not answered is not an entitlement that
  // granted anything, and the first version of this line got that backwards: with getEntitlement still in
  // flight the card called listTestRuns five times and Run tests called runTests (Astra, 2026-09-15). The
  // engine refused every one, so all the mistake bought was a refusal printed where a plan should be.
  const testsGrant = useFeature('tests');
  const [checks, reloadChecks] = useRemote<ChecksHistory>(() => rpc<ChecksHistory>('listTestRuns', 1), keys, testsGrant !== 'granted');
  // Primer suggestions stay FREE and lost their only home when Model notes became Pro, so their list and
  // their Accept and Reject controls live here, beside the instructions editor this card already owns.
  const [suggestions, reloadSuggestions] = useRemote<PrimerSuggestionList>(() => rpc<PrimerSuggestionList>('listPrimerSuggestions'), keys);

  const { context } = useConnection();
  const graph = load.status === 'ready' ? load.graph : null;
  const tables = graph?.tables ?? [];
  const measures = tables.reduce((count, table) => count + table.measures, 0);
  const report = storage?.scanState?.report ?? null;
  const scanTables = report?.tables ?? null;

  const band = useMemo(() => memoryBand({ tables, scanTables }), [tables, scanTables]);
  const rows = useMemo(() => tableRows({ tables, scanTables, scanColumns: report?.topColumns ?? null }), [tables, scanTables, report]);
  const totalRows = useMemo(() => (scanTables ?? []).reduce((n, t) => n + (t.rows || 0), 0), [scanTables]);

  const written = instructions.status === 'ready' && instructions.value.present ? (instructions.value.instructions || '') : '';
  const clauses = heroClauses({
    // The read state travels with the counts. Without it the hero printed "0 tables, 0 measures and 0
    // relationships" over the loading spinner and over the failure panel, which reads as a measurement.
    state: load.status,
    tables: tables.length, measures, relationships: graph?.relationships.length ?? 0,
    memoryBytes: report ? report.modelSize : null,
    largestTable: band.basis === 'memory' ? band.blocks[0]?.name ?? null : null,
    largestShare: band.basis === 'memory' ? band.blocks[0]?.share ?? 0 : 0,
    instructionCount: instructionCount(written),
    sessionEdits,
  });

  const picked = selected && selected.kind !== 'model' ? selected : undefined;
  const action = (tool: string, target?: string, addTables?: string[]) => onGo(tool, target, addTables);
  const pickTable = (ref: string, name: string) => { selectInProperties(ref); onSelect({ kind: 'table', table: name }); };

  return <div className="sem-evidence-page model-home overview">
    {/* No action row here on purpose: Diagram, Lineage and Find and replace are segments in the row directly
        above this page, and repeating them made Model home look like it offered a second, different route. */}
    <section className="overview-hero">
      <div className="overview-hero-text">
        <div className="page-kicker">MODEL</div>
        <h1>{modelName || 'Model'}</h1>
        {clauses.length > 0 && <p>{clauses.map((clause, index) => index === 0
          ? <b key={clause}>{clause} </b>
          : <span key={clause}>{clause} </span>)}</p>}
      </div>
      <div className="overview-facts">
        <RaiseLevelAction key={sessionId} level={compatibilityLevel} onRaised={() => setRetry((value) => value + 1)} />
        <ReadinessTile state={readiness} />
        {report && <Fact value={formatCount(totalRows)} label="rows loaded" spoken={`${totalRows.toLocaleString('en-US')} rows loaded`} />}
        {report && <Fact value={formatBytes(report.modelSize)} label="in memory" spoken={`${formatBytes(report.modelSize)} in memory`} />}
      </div>
    </section>

    {load.status === 'loading' && <div className="model-home-state" role="status"><span className="sem-spin" /> <strong>Loading model structure…</strong><span>Reading the current session.</span></div>}
    {load.status === 'error' && <div className="model-home-state model-home-error" role="alert"><strong>Couldn’t load this model</strong><span>{load.message}</span><button type="button" className="sem-btn" onClick={() => setRetry((value) => value + 1)}>Try again</button></div>}

    {graph && <>
      <MemoryBand band={band} storage={storage} scannedAt={storage?.scanState?.at ?? null}
        connectionName={context?.querying?.available ? context.querying.modelName : null}
        caveat={report?.caveat ?? null} />

      <div className="overview-cols">
        <InstructionsCard state={instructions} onReload={reloadInstructions} onNotes={() => action('knowledge')}
          suggestions={suggestions} onSuggestionsChanged={() => { reloadSuggestions(); reloadInstructions(); }} />
        <section className="overview-card overview-tables">
          <div className="overview-card-head">
            <div>
              <h2>Tables</h2>
              <div className="overview-sub">{band.basis === 'memory'
                ? 'Pick a table to see its actions. Bars show data, dictionary and hash indexes, the same three parts Size by table measures.'
                : 'Pick a table to see its actions. Bars show each table’s share of the model by columns until a size scan is run.'}</div>
            </div>
            {/* The mockup put a Size by table button here. It is deliberately absent: that tool is a segment in the
                row directly above this page, and repeating a segment inside the page is the exact thing that made
                Model home look like it offered a second, different route. The subtitle names the tool instead, and
                the picked table's own strip below carries Table size. */}
          </div>
          {rows.length === 0
            ? <div className="model-empty"><strong>Add a table to begin</strong><span>This model is open and has no tables.</span></div>
            : <>
              <div className="overview-rows-head" aria-hidden="true"><span>Table</span><span>{band.basis === 'memory' ? 'Memory' : 'Size'}</span><span className="overview-col-measures">Measures</span><span>Columns</span><span /></div>
              <div className="overview-rows">
                {rows.map((row) => <TableRow key={row.name} row={row} basis={band.basis}
                  // The row you picked is the row the action strip below is acting on. Unmarked, the strip looked
                  // like it belonged to nothing in the list (M18, walkthrough 2026-09-14).
                  isSelected={picked?.kind === 'table' && picked.table === row.name}
                  onPick={() => row.ref && pickTable(row.ref, row.name)}
                  onPreview={() => row.ref && action('data', row.ref)} />)}
              </div>
            </>}
          {picked && <ObjectActions sessionId={sessionId} object={picked} onGo={action} />}
          {!picked && <div className="overview-hint">Pick a table above, or a measure in the model tree beside the editor, and its actions appear here.</div>}
        </section>
      </div>

      <div className="overview-health">
        <ChecksCard state={checks} grant={testsGrant} onReload={reloadChecks} onOpen={() => action('tests')} />
        {/* ACTIVITY_CAP mirrors App.tsx's own slice(0, 200): at the cap the count is a floor, and the card says
            "or more" instead of printing a total it cannot have. */}
        <ChangesCard edits={sessionEdits} capped={sessionEdits >= ACTIVITY_CAP} onReview={() => action('history')} />
        <PublishedCard agentPolicy={agentPolicy} context={context} onConnections={onConnections} onLineage={() => action('lineage', 'model:')} />
      </div>
    </>}
  </div>;
}

/** Raise compatibility level. Shown only while the level blocks a NAMED capability, so it answers a problem
 *  instead of advertising a number. It is free, and it lives here because its only other control sits inside
 *  Advanced Modelling, which the free plan does not reach.
 *
 *  Keyed by session at the call site: `done` is about the model that was raised, and ModelHome is reused
 *  across models (App.tsx renders it unkeyed), so without that key a raise on one model would hide the
 *  prompt on the next one. */
function RaiseLevelAction({ level, onRaised }: { level?: number; onRaised: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const blocked = blockedCapability(level);
  if (!blocked || done) return null;
  const raise = async () => {
    setBusy(true); setError(null);
    try { await rpc('setCompatibilityLevel', blocked.floor); setDone(true); onRaised(); }
    catch (raiseError) { setError(messageOf(raiseError)); }
    finally { setBusy(false); }
  };
  return <div className="overview-fact overview-fact-quiet">
    {/* Names the ONE thing that is actually refused. Everything else at this level, the classic date table
        included, keeps working, so the sentence must not imply otherwise. */}
    <div className="overview-fact-l">This model is set to level {level}. {blocked.capability} need level {blocked.floor}.</div>
    <button type="button" className="sem-btn sem-btn-sm" data-testid="overview-raise-level" disabled={busy}
      onClick={() => void raise()}>{busy ? 'Raising…' : 'Raise compatibility level'}</button>
    {error && <div className="overview-error" role="alert">The level was not changed. {error}</div>}
  </div>;
}

function Fact({ value, label, spoken, tone }: { value: string; label: string; spoken: string; tone?: string }) {
  return <div className="overview-fact">
    <div className="overview-fact-n" style={tone ? { color: tone } : undefined} aria-hidden="true">{value}</div>
    <div className="overview-fact-l" aria-hidden="true">{label}</div>
    <span className="sr-only">{spoken}</span>
  </div>;
}

/** Ready for AI. The score is cached by the engine, so it usually lands at once; while it does not, the tile says
 *  it is checking rather than showing a grade nobody measured. A scan that fails takes its tile away with it. */
function ReadinessTile({ state }: { state: Remote<ReadinessCard> }) {
  // 'off' cannot happen here (the readiness scan is free and is never suppressed), but the tile must not
  // print a grade from a read that did not run, so it is handled beside the failure rather than assumed away.
  if (state.status === 'error' || state.status === 'off') return null;
  if (state.status === 'loading') {
    return <div className="overview-fact overview-fact-quiet" role="status">
      <div className="overview-fact-n"><span className="sem-spin" /></div>
      <div className="overview-fact-l">Checking… Ready for AI</div>
    </div>;
  }
  const grade = state.value.grade;
  const score = state.value.overall;
  if (!grade && score == null) return null;
  const shown = grade && score != null ? `${grade} · ${score}` : grade || String(score);
  return <Fact value={shown} label="Ready for AI" tone="var(--sem-accent)"
    spoken={`Ready for AI: grade ${grade ?? 'not graded'}, score ${score ?? 'unknown'} out of 100`} />;
}

/** Where the memory goes. One full-width bar, a block per table, sized by memory when a scan exists and by
 *  columns when none does. The caption always names which of the two you are looking at. */
function MemoryBand({ band, storage, scannedAt, connectionName, caveat }: {
  band: ReturnType<typeof memoryBand>; storage: OverviewStorage | null; scannedAt: number | null;
  connectionName?: string | null; caveat: string | null;
}) {
  const ref = useRef<HTMLDivElement | null>(null);
  const [width, setWidth] = useState(1200);
  useEffect(() => {
    const el = ref.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver((entries) => { for (const entry of entries) setWidth(entry.contentRect.width); });
    observer.observe(el);
    setWidth(el.getBoundingClientRect().width);
    return () => observer.disconnect();
  }, []);

  const geometry = useMemo(() => bandGeometry(band.blocks, width), [band, width]);
  const measured = band.basis === 'memory';
  const kinds: TableKind[] = ['data', 'date', 'calculated', 'fieldParameter'];
  const present = kinds.filter((kind) => band.blocks.some((block) => block.kind === kind));
  const when = scannedAt ? new Date(scannedAt).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) : null;

  return <section className="overview-card overview-band" aria-label="Where the memory goes">
    <div className="overview-card-head">
      <h2>{measured ? 'Where the memory goes' : 'What is in the model'}</h2>
      <span className="overview-sub">{measured
        ? `Each block is a table, sized by memory. Measured on ${connectionName || 'the connected model'}${when ? ` at ${when}` : ''}.`
        : 'Each block is a table, sized by how many columns it holds.'}</span>
    </div>
    {/* The block's WIDTH is its share of the band, nothing else: flex growth over a padding-free block, with
        the label's padding moved inside. The geometry function models the same resolution the browser runs, so
        the label decides on the width the block will really draw rather than on a second, different sum. */}
    <div className="overview-comp" ref={ref}>
      {geometry.map((laid, index) => {
        const block = band.blocks[index];
        const detail = measured
          ? `${formatBytes(block.bytes || 0)} · ${Math.round(block.share)}%`
          : `${block.columns} columns`;
        return <span key={block.name} className="overview-comp-block" style={{ flexGrow: block.share, background: KIND_COLOR[block.kind] }}
          title={`${block.name} · ${KIND_TEXT[block.kind]} · ${detail}`}>
          {laid.label !== 'none' && <span className="overview-comp-label">
            <b>{block.name}</b>
            {laid.label === 'full' && <small>{detail}</small>}
          </span>}
        </span>;
      })}
    </div>
    <div className="overview-legend">
      {present.map((kind) => <span key={kind}><i style={{ background: KIND_COLOR[kind] }} />{KIND_TEXT[kind]}</span>)}
    </div>
    {!measured && <div className="overview-foot">
      <span>No size scan yet. Blocks show columns.</span>
      {storage && <button type="button" className="sem-btn sem-btn-sm" disabled={storage.busy} onClick={() => void storage.scan()}>
        {storage.busy ? 'Scanning…' : 'Scan sizes'}</button>}
    </div>}
    {measured && caveat && <div className="overview-foot"><span>{caveat}</span></div>}
    {storage?.err && <div className="overview-error" role="alert">{storage.err}</div>}
  </section>;
}

function TableRow({ row, basis, isSelected, onPick, onPreview }: {
  row: OverviewRow; basis: 'memory' | 'columns'; isSelected: boolean; onPick: () => void; onPreview: () => void;
}) {
  const size = basis === 'memory'
    ? `${formatBytes(row.bytes || 0)}${row.rows != null ? `, ${formatCount(row.rows)} rows` : ''}`
    : `${row.columns} of the model’s columns`;
  return <div className={isSelected ? 'overview-row is-selected' : 'overview-row'} aria-current={isSelected ? 'true' : undefined}>
    {/* The kind rides on the label rather than inside the button, so the row's accessible name stays the table
        name a person would say, and the colour dot is not the only place the kind is stated. */}
    <button type="button" className="overview-row-name" onClick={onPick} aria-label={`${row.name}, ${KIND_TEXT[row.kind]}`}>
      <i style={{ background: KIND_COLOR[row.kind] }} aria-hidden="true" />{row.name}
    </button>
    <div className="overview-stack" title={`${row.name} · ${size}`}>
      {row.segments.map((segment) => segment.width > 0
        ? <i key={segment.part} title={PART_TEXT[segment.part]} style={{ width: `${segment.width}%`, background: PART_COLOR[segment.part] }} />
        : null)}
    </div>
    <span className="overview-n overview-col-measures tnum">{row.measures}</span>
    <span className="overview-n tnum">{row.columns} columns</span>
    <button type="button" className="overview-go" onClick={onPreview} aria-label={`Preview data in ${row.name}`} title="Preview data">›</button>
  </div>;
}

/** AI Instructions (Kane's name, 15 Sep). The instructions saved in the model, read and written through the same engine
 *  path the assistant's own instructions op uses, so the two doors cannot drift apart. */
function InstructionsCard({ state, onReload, onNotes, suggestions, onSuggestionsChanged }: {
  state: Remote<AiInstructions>; onReload: () => void; onNotes: () => void;
  suggestions: Remote<PrimerSuggestionList>; onSuggestionsChanged: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [deciding, setDeciding] = useState<string | null>(null);
  const [decideError, setDecideError] = useState<string | null>(null);
  const value = state.status === 'ready' ? state.value : null;
  const waiting = suggestions.status === 'ready' ? (suggestions.value.suggestions ?? []) : [];
  const decide = async (id: string, accept: boolean) => {
    setDeciding(id); setDecideError(null);
    // Two literal calls, not one call with a computed name. rpc-name-parity.test.mjs reads these names out
    // of the source, and a name built at runtime is a name no gate can check against the engine.
    try {
      if (accept) await rpc('acceptPrimerSuggestion', id, 'human');
      else await rpc('rejectPrimerSuggestion', id, 'human');
      onSuggestionsChanged();
    }
    catch (error) { setDecideError(messageOf(error)); }
    finally { setDeciding(null); }
  };
  const limit = value?.limit || 2000;
  const text = value?.present ? (value.instructions || '') : '';

  const open = () => { setDraft(text); setSaveError(null); setEditing(true); };
  const save = async () => {
    setSaving(true); setSaveError(null);
    try { await rpc('setAiInstructions', draft); setEditing(false); onReload(); }
    catch (error) { setSaveError(messageOf(error)); }
    finally { setSaving(false); }
  };

  return <section className="overview-card overview-instructions">
    <div className="overview-card-head">
      <div>
        <h2>AI Instructions</h2>
        <div className="overview-sub">The instructions saved in this model. Every question your assistant answers starts from these.</div>
      </div>
      {state.status === 'ready' && !editing && value?.present
        && <button type="button" className="sem-btn sem-btn-sm" onClick={open}>Edit</button>}
    </div>

    <div className="overview-instructions-body"><div className="overview-instructions-scroll">
    {state.status === 'loading' && <div className="overview-quiet" role="status"><span className="sem-spin" /> Reading the instructions…</div>}
    {state.status === 'error' && <div className="overview-error" role="alert">
      <span>The instructions could not be read. {state.message}</span>
      <button type="button" className="sem-btn sem-btn-sm" onClick={onReload}>Try again</button>
    </div>}

    {editing && <div className="overview-editor">
      <label className="sr-only" htmlFor="overview-instructions-editor">Instructions for your assistant</label>
      <textarea id="overview-instructions-editor" value={draft} autoFocus spellCheck
        onChange={(event) => setDraft(event.target.value)} />
      <div className="overview-editor-foot">
        <span className={draft.length > limit ? 'overview-over tnum' : 'tnum'}>{draft.length.toLocaleString('en-US')} of {limit.toLocaleString('en-US')} characters</span>
        <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={saving || draft.length > limit} onClick={() => void save()}>{saving ? 'Saving…' : 'Save'}</button>
        <button type="button" className="sem-btn sem-btn-sm" disabled={saving} onClick={() => setEditing(false)}>Cancel</button>
      </div>
      {draft.length > limit && <div className="overview-error">That is longer than this model accepts. Shorten it to {limit.toLocaleString('en-US')} characters.</div>}
      {saveError && <div className="overview-error" id="overview-instructions-error" role="alert">The instructions were not saved. {saveError}</div>}
    </div>}

    {!editing && state.status === 'ready' && value?.present && <>
      <div className="overview-instr">{text}</div>
      <div className="overview-meter">
        <span className="tnum">{(value.length || text.length).toLocaleString('en-US')} of {limit.toLocaleString('en-US')} characters</span>
        <div className="overview-meter-bar"><i style={{ width: `${Math.min(100, (100 * (value.length || text.length)) / limit)}%` }} /></div>
        {value.culture && <span>Written for {value.culture}</span>}
      </div>
      {value.note && <div className="overview-quiet">{value.note}</div>}
    </>}

    {!editing && state.status === 'ready' && !value?.present && <div className="overview-empty">
      <strong>Nothing written yet.</strong>
      <span>Your assistant answers from the model alone. Write the first instructions.</span>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" onClick={open}>Write instructions</button>
    </div>}
    </div></div>

    {/* Suggested additions to the written background. Their Accept and Reject buttons used to sit inside the
        Model notes page, which is Pro now; the decisions themselves are free, so they live here. */}
    {waiting.length > 0 && <div className="overview-suggestions">
      <div className="overview-sub">
        {waiting.length === 1 ? 'One suggested note is waiting for you.' : `${waiting.length} suggested notes are waiting for you.`}
        {' '}Each is added only when you select Accept.
      </div>
      {decideError && <div className="overview-error" role="alert">That was not saved. {decideError}</div>}
      {waiting.map((suggestion) => <div key={suggestion.id} className="overview-suggestion" data-testid="overview-suggestion">
        <div className="overview-suggestion-text">
          <b>{suggestion.section}</b>
          <span>{suggestion.markdown.split('\n')[0].replace(/^[-*]\s*/, '')}</span>
        </div>
        <div className="overview-suggestion-actions">
          <button type="button" className="sem-btn sem-btn-sm" data-testid="overview-suggestion-reject"
            disabled={!!deciding} onClick={() => void decide(suggestion.id, false)}>Reject</button>
          <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="overview-suggestion-accept"
            disabled={!!deciding} onClick={() => void decide(suggestion.id, true)}>{deciding === suggestion.id ? 'Saving…' : 'Accept'}</button>
        </div>
      </div>)}
    </div>}

    <div className="overview-notes">Notes for people are separate. <button type="button" className="overview-link" onClick={onNotes}>Open Model notes</button></div>
  </section>;
}

/** Checks. The card shows the run you triggered HERE, and falls back to saved history only when you have not
 *  run anything on this page. Run tests used to await the run, discard it and re-read the saved history; the
 *  RPC does not persist by default and the engine only appends history when it does, so a fresh run of 23
 *  failures left "23 of 23 pass" on screen, which is why the fix reads the returned run.
 *
 *  THREE states, because "not denied" is not "granted". `granted` is the real card; `denied` makes no engine
 *  call of any kind and Run tests opens the Tests preview instead; `unknown` is the plan not having answered
 *  yet, and it behaves exactly like denied for everything paid while saying something truer than either. */
function ChecksCard({ state, grant, onReload, onOpen }: {
  state: Remote<ChecksHistory>; grant: FeatureGrant; onReload: () => void; onOpen: () => void;
}) {
  const locked = grant === 'denied';
  const pending = grant === 'unknown';
  const paid = grant === 'granted';
  const [running, setRunning] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  const [latest, setLatest] = useState<ChecksRun | null>(null);
  const run = async () => {
    if (!paid) return;   // belt on top of the disabled button: a press must never outrun the plan
    setRunning(true); setRunError(null);
    // No persist argument on purpose: the card reads the run it got BACK rather than asking the engine to
    // write it down and then re-reading the saved history, which is how a fresh run of 23 failures once left
    // "23 of 23 pass" on screen. Saving a run is a separate choice on the Tests page.
    try { const result = await rpc<ChecksRun>('runTests'); setLatest(result); if (result?.persisted) onReload(); }
    catch (error) { setRunError(messageOf(error)); }
    finally { setRunning(false); }
  };
  const card = paid && (state.status === 'ready' || latest) ? checksCard({ history: state.status === 'ready' ? state.value : null, latest }) : null;
  return <section className="overview-card overview-health-card">
    <div className="page-kicker">CHECKS {locked ? <ProBadge show /> : null}</div>
    {pending && <div className="overview-quiet" role="status"><span className="sem-spin" /> {PENDING_ACCESS}</div>}
    {locked && <>
      <div className="overview-v">Tests is a Pro feature.</div>
      <div className="overview-d">Model quality and AI understanding still check your model for free. Tests compares your numbers against the source and lets you save the results.</div>
    </>}
    {paid && state.status === 'loading' && !latest && <div className="overview-quiet" role="status"><span className="sem-spin" /> Checking…</div>}
    {paid && state.status === 'error' && !latest && <div className="overview-error" role="alert">
      <span>The last test run could not be read. {state.message}</span>
      <button type="button" className="sem-btn sem-btn-sm" onClick={onReload}>Try again</button>
    </div>}
    {paid && card && <>
      <div className={card.state === 'warn' ? 'overview-v overview-v-warn' : card.state === 'ok' ? 'overview-v overview-v-ok' : 'overview-v'}>{card.headline}</div>
      <div className="overview-d">{card.detail}</div>
      {/* Which run you are looking at. Without it a person cannot tell a run from this minute apart from one
          recorded last Friday, and the two can disagree completely. */}
      {card.source && <div className="overview-stamp">{card.source}</div>}
      {card.note && <div className="overview-quiet">{card.note}</div>}
      {card.error && <div className="overview-error" role="alert">{card.error}</div>}
    </>}
    {paid && runError && <div className="overview-error" role="alert">The tests did not run. {runError}</div>}
    <div className="overview-actions">
      {/* On the free plan this is a way INTO the Tests page, where the preview explains the feature. It does
          not call runTests, because running the checks is the paid thing. */}
      {/* A run can only start on a plan that is KNOWN to grant Tests. While the plan is unknown the control
          keeps its place, says what it is waiting for and is disabled, because a press would spend a paid
          call on a guess. On the free plan it is a way into the preview and calls nothing. */}
      <button type="button" className="sem-btn sem-btn-sm" data-testid="overview-checks-run" disabled={pending || (paid && running)}
        onClick={() => { if (locked) onOpen(); else if (paid) void run(); }}>
        {pending ? PENDING_ACCESS : locked ? 'See Tests' : running ? 'Running…' : 'Run tests'}</button>
      <button type="button" className="sem-btn sem-btn-sm" onClick={onOpen}>Open Checks</button>
    </div>
  </section>;
}

/** Changes. It counts the edits this page has SEEN and says so in those words. It used to announce that there
 *  was nothing waiting and that every edit had already reached the destination, reading that from a list which
 *  starts empty and only ever holds this webview's own change notifications: a reload alone was enough to make
 *  the claim about a model with unsaved edits. The comparison that could actually answer the question lives
 *  behind Review changes and the publish step, so the card names its own number and points there. */
function ChangesCard({ edits, capped, onReview }: { edits: number; capped: boolean; onReview: () => void }) {
  const card = changesCard({ edits, capped });
  return <section className="overview-card overview-health-card">
    <div className="page-kicker">CHANGES</div>
    <div className="overview-v">{card.headline}</div>
    <div className="overview-d">{card.detail}</div>
    <div className="overview-actions">
      <button type="button" className="sem-btn sem-btn-sm" onClick={onReview}>Review changes</button>
    </div>
  </section>;
}

function PublishedCard({ agentPolicy, context, onConnections, onLineage }: {
  agentPolicy: AgentPolicy | null | undefined;
  context: ReturnType<typeof useConnection>['context'];
  onConnections: () => void; onLineage: () => void;
}) {
  const publishing = context?.publishing;
  const target = publishing?.available ? (publishing.modelName || publishing.database || 'linked model') : null;
  const label = publishing?.effectiveLabel || publishing?.label;
  const stage = label ? publishStageName(label) : (publishing?.unlabelled ? 'production' : 'destination needed');
  const permission = agentPolicy ? publishPermissionDescription(agentPolicy, label, !!target)
    : agentPolicy === null ? 'Your assistant permissions could not be read' : 'Checking your assistant permissions';
  return <section className="overview-card overview-health-card">
    <div className="page-kicker">PUBLISHED</div>
    {context == null
      ? <div className="overview-quiet" role="status"><span className="sem-spin" /> Reading the connection…</div>
      : <>
        <div className="overview-v">{target ? `${target} · ${stage}` : 'No publish target yet'}</div>
        <div className="overview-d">{target
          ? `${permission.charAt(0).toUpperCase()}${permission.slice(1)}.`
          : 'Choose where this model publishes before you publish it.'}</div>
      </>}
    <div className="overview-actions">
      <button type="button" className="sem-btn sem-btn-sm" onClick={onConnections}>Connections and accounts</button>
      <button type="button" className="sem-btn sem-btn-sm" onClick={onLineage}>Dependencies</button>
    </div>
  </section>;
}

function ObjectActions({ sessionId, object, onGo }: { sessionId: string; object: ObjectRef; onGo: (tool: string, target?: string, addTables?: string[]) => void }) {
  const ref = objectToRef(object);
  const name = objectLabel(object) || 'Model';
  let primary: React.ReactNode;
  let also: React.ReactNode;
  if (object.kind === 'table') {
    primary = <><button className="sem-primary" onClick={() => onGo('data', ref)}>Preview data</button><button onClick={() => onGo('mcode', ref)}>Edit Power Query</button><button onClick={() => onGo('diagram', ref, [object.table])}>Show in diagram</button></>;
    also = <><button onClick={() => onGo('lineage', ref)}>Lineage</button><button onClick={() => onGo('stats', ref)}>Table size</button><button onClick={() => onGo('search', ref)}>Find in this table</button><button onClick={() => runHostCommand('semanticus.newMeasure', ref, sessionId)}>Add measure</button></>;
  } else if (object.kind === 'measure') {
    primary = <><button className="sem-primary" onClick={() => runHostCommand('semanticus.editDax', ref, sessionId)}>Edit formula</button><button onClick={() => onGo('daxlab', ref)}>Try calculation</button><button onClick={() => onGo('tests', ref)}>Add test</button></>;
    also = <><button onClick={() => onGo('lineage', ref)}>Lineage</button><button onClick={() => onGo('search', ref)}>Find and replace</button><button onClick={() => onGo('history', ref)}>History</button></>;
  } else if (object.kind === 'relationship') {
    primary = <><button className="sem-primary" onClick={() => onGo('diagram', ref)}>Edit in diagram</button><button onClick={() => onGo('lineage', ref)}>Lineage</button></>;
    also = null;
  } else if (object.kind === 'column' || object.kind === 'calcColumn') {
    primary = <><button className="sem-primary" onClick={() => onGo('data', ref)}>Preview data</button><button onClick={() => onGo('lineage', ref)}>Lineage</button></>;
    also = <><button onClick={() => { selectInProperties(ref); focusSelectInProperties(ref); }}>Hide or show</button><button onClick={() => onGo('diagram', ref, [object.table])}>Show in diagram</button></>;
  } else {
    primary = <><button className="sem-primary" onClick={() => onGo('lineage', ref)}>Lineage</button><button onClick={() => { selectInProperties(ref); focusSelectInProperties(ref); }}>Properties</button></>;
    also = null;
  }
  return <section className="object-actions"><div className="object-action-title"><div className="page-kicker">{object.kind.toUpperCase()}</div><strong>{name}</strong></div><div className="object-action-content"><div className="object-action-buttons">{primary}</div>{also && <div className="object-action-also"><span>Also:</span>{also}</div>}</div></section>;
}
