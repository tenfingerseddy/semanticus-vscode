import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { rpc } from './bridge';
import type { ResultSet } from './wire';
import { ResultGrid } from './grid';
import { gen, mString, letShape, renameStep, deleteStep, M_TYPES, type TransformResult } from './mtransform';
import { createContextBusyOwnerGate, createRequestGate, transformErrorForAction, transformErrorKey } from './mcodelifecycle.mjs';

// ===================================================================================================
// M Code tab — the "UI writes M for you" surface (docs/pq-transforms-plan.md §1–3). Everything here
// is deterministic text transformation over the editor's M (the [F] kernel in mtransform.ts) plus ONE
// read-only DAX profile against the LOADED table. There is no cross-platform Mashup engine, so we never
// evaluate M per-step: generated steps take effect at the NEXT refresh; the sample shows loaded data.
//   • SamplePreview — the loaded-data sample grid whose column HEADERS are the interaction surface: a type
//     glyph + name, a ⌄ / right-click per-column ops menu, and a lifecycle-driven profiling row.
//     A slim table-level transform bar (remove duplicates / keep top N + the Profile action) sits on top.
//   • AppliedStepsPanel — the interactive Applied-Steps outline (rename / delete / click-to-select).
// ===================================================================================================

// shared inline styles (mirrors mcode.tsx — kept local so this module stands alone)
const input: React.CSSProperties = { background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, fontSize: 11, padding: '2px 6px', maxWidth: 220 };
const btn: React.CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, fontSize: 11, padding: '3px 10px', cursor: 'pointer' };
const primaryBtn: React.CSSProperties = { ...btn, background: 'var(--sem-accent-soft)', borderColor: 'var(--sem-accent)' };
const linkBtn: React.CSSProperties = { background: 'transparent', border: 'none', color: 'var(--sem-accent)', cursor: 'pointer', fontSize: 11, padding: 0 };
const hint: React.CSSProperties = { color: 'var(--sem-muted)', fontSize: 10.5 };

// Dual identity: `name` is the MODEL name (display, DAX profile refs, doc lookups); `mName` is the
// PARTITION-OUTPUT name M operates on (TOM DataColumn.SourceColumn — can legitimately differ from Name).
// mName undefined ⇒ the M-side identity is unknown and the per-column M ops stand down for that column;
// `calc` says WHY when the column is calculated (not in the partition output at all).
export interface GridCol { name: string; type?: string; mName?: string; calc?: boolean }

// THE source-name rule (single source of truth), shared by the doc-column mapping AND the range-filter
// generation. M writes against the PARTITION-OUTPUT field (TOM DataColumn.SourceColumn), never the model Name:
//   • calculated column        → undefined  (not in the partition output at all — M ops stand down)
//   • sourceColumn ABSENT       → name       (an older engine that predates the field; the model name is the
//                                             only signal, and only for a DATA column)
//   • sourceColumn null / empty → undefined  (a wire-declared "no source name" NEVER falls back to the model name)
//   • sourceColumn populated    → sourceColumn
// Exported so the test harness can execute the rule directly.
export function sourceMName(col: { name: string; isCalculated?: boolean; sourceColumn?: string | null }): string | undefined {
  if (col.isCalculated) return undefined;
  return col.sourceColumn === undefined ? col.name : (col.sourceColumn || undefined);
}

// Live sample columns carry no SourceColumn (ADOMD labels are model names); resolve the M-side name via the
// doc model by MODEL name (the doc GridCols already carry their mName via sourceMName above). No doc match ⇒
// undefined: the M ops stand down rather than write M against a guessed name. Exported so the harness executes it.
export function resolveMName(modelName: string, docColumns: GridCol[]): string | undefined {
  return docColumns.find((d) => d.name === modelName)?.mName;
}

const isNumType = (t?: string) => !!t && /int|dec|doub|number|curr|float|real|money/i.test(t);

// ---- transform bar + per-column menu + profiling strip ------------------------------------------------

type FilterOp = 'equals' | 'notEquals' | 'contains' | 'beginsWith' | 'greater' | 'less' | 'nonEmpty';
const NUM_OPS: [FilterOp, string][] = [['equals', '='], ['notEquals', '≠'], ['greater', '>'], ['less', '<'], ['nonEmpty', 'is not empty']];
const TXT_OPS: [FilterOp, string][] = [['equals', 'equals'], ['notEquals', 'does not equal'], ['contains', 'contains'], ['beginsWith', 'begins with'], ['nonEmpty', 'is not empty']];

interface ProfileCell { distinct: number; nulls: number; min?: number; max?: number }
// data = the columns that profiled; failed = column name → why it was skipped (per-column, so one bad column
// degrades instead of nuking the strip); error = a whole-strip failure (the table itself couldn't be queried).
type ProfileStatus = 'idle' | 'running' | 'results' | 'failed' | 'stale';
interface ProfileState {
  status: ProfileStatus;
  total: number | null;
  data: Record<string, ProfileCell>;
  failed: Record<string, string>;
  error: string | null;
  activeColumns: string[];
  progress: { completed: number; total: number } | null;
}
type TransformAction = 'remove-duplicates' | 'keep-top' | 'remove-column' | 'rename-column' | 'change-type' | 'filter-rows' | 'replace-values' | 'sort-ascending' | 'sort-descending' | 'trim-clean';
interface TransformOperation { resource: string; action: TransformAction; label: string }
type TransformBusy = TransformOperation & { ownerId: number; contextToken: string };

// IFERROR sentinel: an aggregate the engine could EVALUATE but that errored (e.g. an erroring calc column) yields
// this instead of nuking the row. DISTINCTCOUNT/COUNTBLANK are always ≥0, so -1 is an unambiguous "skip me".
const PROFILE_ERR = -1;
const PROFILE_LIMIT = 12;
// Columns per probe query. IFERROR guards eval-time errors inside a chunk; a chunk that HARD-fails (a binary/
// unsupported column rejected at BIND time — IFERROR can't catch those) bisects to solo probes so only the one
// offending column is skipped and its real error message becomes the reason.
const PROFILE_CHUNK = 4;

// ADOMD labels a live result's table columns 'Table[Column]' (a ']' inside the name escaped as ']]'); the model
// column name is the FINAL bracket pair's content. Doc-model names are already bare and pass through (no
// trailing ']' means no match). Exported so the test harness can execute it.
export function bareColumnName(label: string): string {
  const m = /\[((?:\]\]|[^\]])*)\]$/.exec(label);
  return m ? m[1].replace(/\]\]/g, ']') : label;
}

// pull a named scalar from a single-row result, bracket-insensitive. null/undefined (DAX BLANK) and absent
// slots are ABSENT (NaN), never coerced — Number(null) is 0, which would let an all-blank column masquerade
// as "min 0 · max 0".
function profileScalar(r: ResultSet, key: string): number {
  const idx = r.columns.findIndex((c) => c.name.replace(/[[\]]/g, '') === key);
  const v = idx >= 0 ? r.rows?.[0]?.[idx] : undefined;
  if (v == null || v === '') return NaN;
  const n = Number(v);
  return Number.isFinite(n) ? n : NaN;
}

// Errors that indict the CONNECTION (or the whole query path), not one column. These stop the profile
// immediately — fanning out per-column probes against a dead endpoint would be a 2N-query storm for nothing.
// Quoted identifiers ('...' with '' escapes, [...] with ]] escapes) are stripped FIRST so a column named
// 'Connection Timeout' or [Session]] Connection Lost] can't take the strip-level exit; the patterns are
// phrase-shaped ("connection … lost" within one clause), not keyword soup, and bare 5xx numbers don't count —
// "503 Service Unavailable" classifies via its words, "line 503" doesn't. Honest follow-up: a structured
// error kind from the engine would replace this message heuristic. Exported so the test harness executes it.
export function connectionShaped(raw: string): boolean {
  const msg = raw.replace(/'(?:''|[^'])*'|\[(?:\]\]|[^\]])*\]/g, ' ');
  return (/\bnot connected\b|\bconnection\b[^.!?;]{0,40}?\b(?:lost|closed|refused|failed|broken|reset)\b|\bno connection could be made\b|\btarget machine actively refused\b|\bserver did not respond\b|\bremote (?:endpoint|host)\b[^.!?;]{0,40}?\b(?:refused|reset|closed|unreachable|not responding|did not respond)\b|\btimed out\b|\btimeout\b|\bnetwork\b|\btransport\b|\bsession (?:expired|closed|terminated)\b|\bunreachable\b|\bcancell?ed\b|\bbad gateway\b|\b(?:server|service|endpoint|database) (?:is )?(?:currently )?unavailable\b/i).test(msg);
}

interface ProfileResult { total: number | null; data: Record<string, ProfileCell>; failed: Record<string, string>; error: string | null }
interface ProfileProgress extends ProfileResult { completed: number; count: number }

const idleProfile = (): ProfileState => ({
  status: 'idle', total: null, data: {}, failed: {}, error: null, activeColumns: [], progress: null,
});

// Profile the loaded table with fault isolation and an abort seam. A standalone row-count probe runs first (a
// failure there is a STRIP-level error — nothing column-specific to salvage), then IFERROR-guarded chunk
// queries. A chunk that hard-fails with a connection-shaped error stops the whole profile; a column-shaped hard
// fail (bind-time reject — the rows probe already proved the table + connection) bisects to solo probes so only
// the offending column is skipped, with its real error as the reason. isCurrent is checked between every query
// so a stale run (table switch / toggle off / rerun) stops issuing queries; returns null when aborted.
async function runColumnProfile(
  table: string,
  cols: GridCol[],
  isCurrent: () => boolean,
  onProgress?: (progress: ProfileProgress) => void,
): Promise<ProfileResult | null> {
  const data: Record<string, ProfileCell> = {};
  const failed: Record<string, string> = {};
  const strip = (error: string): ProfileResult => ({ total: null, data: {}, failed: {}, error });
  let completed = 0;

  let total: number | null = null;
  try {
    const r = await rpc<ResultSet>('runDax', profileRowsDax(table), 1);
    if (!isCurrent()) return null;
    if (r?.error) return strip(r.error);
    const t = profileScalar(r, 'rows');
    total = Number.isFinite(t) ? t : null;
  } catch (e) {
    return isCurrent() ? strip(String((e as Error).message ?? e)) : null;
  }

  const meta = cols.map((c, i) => ({ name: c.name, i, numeric: isNumType(c.type) }));
  const skipReason = "This column couldn't be profiled (it may be a binary or otherwise non-aggregable type).";
  const report = () => onProgress?.({ total, data: { ...data }, failed: { ...failed }, error: null, completed, count: meta.length });
  report();

  const absorb = (r: ResultSet, group: typeof meta) => {
    group.forEach(({ name, i, numeric }) => {
      const d = profileScalar(r, 'd' + i), b = profileScalar(r, 'b' + i);
      if (!Number.isFinite(d) || d === PROFILE_ERR || !Number.isFinite(b) || b === PROFILE_ERR) { failed[name] = skipReason; return; }
      const cell: ProfileCell = { distinct: d, nulls: b };
      if (numeric) { const mn = profileScalar(r, 'mn' + i), mx = profileScalar(r, 'mx' + i); if (Number.isFinite(mn)) cell.min = mn; if (Number.isFinite(mx)) cell.max = mx; }
      data[name] = cell;
    });
  };

  for (let s = 0; s < meta.length; s += PROFILE_CHUNK) {
    const group = meta.slice(s, s + PROFILE_CHUNK);
    if (!isCurrent()) return null;
    let chunkError = '';
    try {
      const r = await rpc<ResultSet>('runDax', profileChunkDax(table, group), 1);
      if (r?.error) throw new Error(r.error);
      absorb(r, group);
      completed += group.length; report();
      continue;
    } catch (e) { chunkError = String((e as Error).message ?? e); }
    if (!isCurrent()) return null;
    if (connectionShaped(chunkError)) return strip(chunkError);
    // Column-shaped hard fail: bisect to solo probes so only the offending column is lost, with its real reason.
    for (const one of group) {
      if (!isCurrent()) return null;
      try {
        const r = await rpc<ResultSet>('runDax', profileChunkDax(table, [one]), 1);
        if (r?.error) throw new Error(r.error);
        absorb(r, [one]);
      } catch (colErr) {
        const msg = String((colErr as Error).message ?? colErr);
        if (!isCurrent()) return null;
        if (connectionShaped(msg)) return strip(msg);
        failed[one.name] = msg || skipReason;
      }
      completed++; report();
    }
  }
  return { total, data, failed, error: null };
}

/**
 * The loaded-data sample and its transform surface, bundled into ONE block. A slim table-level bar (remove
 * duplicates / keep top N + Profile action + Refresh) sits above the preview grid; the grid's COLUMN HEADERS
 * are the per-column op surface — a type glyph + name, and a ⌄ / right-click menu offering the same ops the
 * kernel already implements (rename / change type / remove / filter / replace / sort / trim). Once Profile is
 * requested, a distinct/null strip docks directly under the headers, column-aligned. Column names come from the loaded
 * sample when present, else the doc model's columns for the table — so the headers (and every op) work OFFLINE;
 * only the row data + profiling need a live connection. Each op runs a kernel generator against the CURRENT
 * editor text and, on ok, replaces it (dirty, exactly like a manual edit — Save stays the explicit act) with a
 * toast; on ok:false the kernel's error is shown verbatim.
 */
export function SamplePreview({ tableName, docColumns, mText, contextToken, isContextCurrent, connected, profileRevision, apply, fail }: {
  tableName: string | null;
  docColumns: GridCol[];                                 // offline fallback columns (doc model) for the selected table
  mText: string;
  contextToken: string;                                  // table + query identity + editor text revision
  isContextCurrent: (captured: string) => boolean;        // reads the parent's synchronously updated context ref
  connected: boolean;
  profileRevision: number;                              // save / format / generated-step invalidation signal
  apply: (m: string, stepName: string) => void;          // ok  → replace editor text + toast
  fail: (error: string) => void;                         // err → show the kernel message verbatim
}) {
  const tableNameRef = useRef(tableName); tableNameRef.current = tableName;
  const [menu, setMenu] = useState<{ ci: number; x: number; y: number } | null>(null);
  const [profile, setProfile] = useState<ProfileState>(idleProfile);
  const profileRef = useRef(profile); profileRef.current = profile;
  const [profileDetails, setProfileDetails] = useState(false);
  const profileDetailsRef = useRef<HTMLDivElement | null>(null);
  const [transformBusy, setTransformBusy] = useState<TransformBusy | null>(null);
  const transformBusyGate = useRef(createContextBusyOwnerGate<TransformOperation>(contextToken));
  const [transformErrors, setTransformErrors] = useState<Record<string, string>>({});
  const profReq = useRef(createRequestGate());

  // The live sample (read-only EVALUATE TOPN of the loaded table — not a refresh; no per-step M eval exists
  // cross-platform). Owned here so the sample + its transform headers are one surface. A request token guards
  // against a stale table's result landing after a switch.
  const [sample, setSample] = useState<ResultSet | null>(null);
  const [sampleErr, setSampleErr] = useState<string | null>(null);
  const [sampleBusy, setSampleBusy] = useState<string | null>(null);
  const sampleBusyRef = useRef<string | null>(null);
  const sampleReq = useRef(0);
  const commitProfile = useCallback((next: ProfileState) => { profileRef.current = next; setProfile(next); }, []);
  const markProfileStale = useCallback(() => {
    const current = profileRef.current;
    if (current.status === 'idle' || current.status === 'stale') return;
    profReq.current.cancel();
    commitProfile({ ...current, status: 'stale', error: null, activeColumns: [], progress: null });
  }, [commitProfile]);
  const loadSample = useCallback(async (invalidateProfile = false) => {
    if (!tableName || !connected) return;
    if (invalidateProfile) markProfileStale();
    const busyKey = `sample:${tableName}`;
    if (sampleBusyRef.current === busyKey) return;
    const req = ++sampleReq.current;
    sampleBusyRef.current = busyKey; setSampleBusy(busyKey); setSampleErr(null);
    try {
      const r = await rpc<ResultSet>('previewTable', tableName, 500);
      if (req !== sampleReq.current) return;
      if (r?.error) { setSampleErr(r.error); setSample(null); }
      else if (r) setSample(r);
      else { setSampleErr('No data returned.'); setSample(null); }
    } catch (e) { if (req === sampleReq.current) setSampleErr(String((e as Error).message ?? e)); }
    finally {
      if (sampleBusyRef.current === busyKey) { sampleBusyRef.current = null; setSampleBusy(null); }
    }
  }, [tableName, connected, markProfileStale]);
  // Reset on table switch and auto-load once when a live engine is present (the Data tab auto-previews the
  // same way) — so the preview headers show live types/rows without a manual step. Offline the headers still
  // render from docColumns.
  useEffect(() => {
    sampleReq.current++; sampleBusyRef.current = null; setSampleBusy(null); setSample(null); setSampleErr(null); setMenu(null);
    transformBusyGate.current.reset(); setTransformBusy(null); setTransformErrors({});
    if (connected && tableName) void loadSample();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tableName, connected]);

  const profileTableRef = useRef(tableName);
  useEffect(() => {
    if (profileTableRef.current === tableName) return;
    profileTableRef.current = tableName; profReq.current.cancel(); setProfileDetails(false); commitProfile(idleProfile());
  }, [tableName, commitProfile]);

  const profileMTextRef = useRef(mText);
  useEffect(() => {
    if (profileMTextRef.current === mText) return;
    profileMTextRef.current = mText; markProfileStale();
  }, [mText, markProfileStale]);

  const profileRevisionRef = useRef(profileRevision);
  useEffect(() => {
    if (profileRevisionRef.current === profileRevision) return;
    profileRevisionRef.current = profileRevision; markProfileStale();
  }, [profileRevision, markProfileStale]);

  useEffect(() => {
    transformBusyGate.current.resetContext(contextToken);
    setTransformBusy(null); setTransformErrors({});
  }, [contextToken]);

  useEffect(() => { if (!connected) markProfileStale(); }, [connected, markProfileStale]);
  useEffect(() => { if (profileDetails) profileDetailsRef.current?.focus(); }, [profileDetails]);
  // Unmount is a real cancellation boundary. A run abandoned by navigation must stop before its next DAX probe,
  // and its completion must not overlap or update a fresh instance after remount.
  useEffect(() => () => {
    profReq.current.cancel(); sampleReq.current++;
    sampleBusyRef.current = null; transformBusyGate.current.reset();
  }, []);

  // Display columns: the loaded sample's (live types) when present, else the doc-model columns for the table.
  // ADOMD labels sample columns 'Table[Column]'; normalize to the bare MODEL name once here (headers, profile
  // keys, DAX refs), then resolve the separate M-side identity (SourceColumn) from the doc model — a sample
  // column with no doc match keeps mName undefined and its M ops stand down.
  const columns = useMemo<GridCol[]>(
    () => (sample?.columns?.length
      ? sample.columns.map((c) => {
          const name = bareColumnName(c.name);
          return { name, type: c.type, mName: resolveMName(name, docColumns), calc: docColumns.find((d) => d.name === name)?.calc };
        })
      : docColumns),
    [sample, docColumns],
  );
  const profileColumnsKey = useMemo(() => columns.map((c) => `${c.name}\u0000${c.type ?? ''}`).join('\u0001'), [columns]);
  const profileColumnsKeyRef = useRef(profileColumnsKey);
  useEffect(() => {
    if (profileColumnsKeyRef.current === profileColumnsKey) return;
    profileColumnsKeyRef.current = profileColumnsKey; markProfileStale();
  }, [profileColumnsKey, markProfileStale]);

  // run a generator; ok → apply, err → surface verbatim
  const run = async (p: Promise<TransformResult>, action: TransformAction, label: string, column?: string): Promise<boolean> => {
    const resource = `preview:${tableName ?? 'none'}`;
    const operationContext = contextToken;
    const errorKey = transformErrorKey(tableName, column, action);
    const owner = transformBusyGate.current.begin({ resource, action, label }, operationContext);
    if (!owner) return false;
    setTransformBusy(owner);
    setTransformErrors((errors) => {
      if (!(errorKey in errors)) return errors;
      const clean = { ...errors }; delete clean[errorKey]; return clean;
    });
    try {
      const r = await p;
      if (!transformBusyGate.current.isOwner(owner)) return false;
      if (resource !== `preview:${tableNameRef.current ?? 'none'}` || !isContextCurrent(operationContext)) {
        fail('Transform result discarded because the table, query, or M text changed while it was running.');
        return false;
      }
      if (r.ok) { apply(r.m, r.step ?? 'Step'); setMenu(null); return true; }
      setTransformErrors((errors) => ({ ...errors, [errorKey]: r.error })); fail(r.error); return false;
    } catch (e) {
      if (!transformBusyGate.current.isOwner(owner)) return false;
      if (resource !== `preview:${tableNameRef.current ?? 'none'}` || !isContextCurrent(operationContext)) {
        fail('Transform result discarded because the table, query, or M text changed while it was running.');
        return false;
      }
      const error = String((e as Error).message ?? e);
      setTransformErrors((errors) => ({ ...errors, [errorKey]: error })); fail(error); return false;
    } finally {
      if (transformBusyGate.current.release(owner)) setTransformBusy(null);
    }
  };

  // Read-only profile of the loaded table (distinct / nulls / numeric min-max per column, up to 12 columns).
  // The user starts every run explicitly. Progress snapshots expose completed chunks while runColumnProfile keeps
  // its row-count-first, chunk, solo-fallback, and connection-failure fault-isolation behavior.
  const visible = useMemo(() => columns.slice(0, PROFILE_LIMIT), [columns]);
  const startProfile = async (retryNames?: string[]) => {
    if (!connected || !tableName || visible.length === 0 || profileRef.current.status === 'running') return;
    const requested = retryNames?.length ? visible.filter((c) => retryNames.includes(c.name)) : visible;
    if (requested.length === 0) return;
    const base = profileRef.current;
    const retrying = !!retryNames?.length;
    const baseData = retrying ? base.data : {};
    const baseFailed = retrying
      ? Object.fromEntries(Object.entries(base.failed).filter(([name]) => !retryNames!.includes(name)))
      : {};
    const activeColumns = requested.map((c) => c.name);
    const req = profReq.current.begin();
    setProfileDetails(false);
    commitProfile({
      status: 'running', total: retrying ? base.total : null, data: baseData, failed: baseFailed,
      error: null, activeColumns, progress: null,
    });
    const res = await runColumnProfile(tableName, requested, () => profReq.current.isCurrent(req), (next) => {
      if (!profReq.current.isCurrent(req)) return;
      commitProfile({
        status: 'running', total: next.total,
        data: retrying ? { ...baseData, ...next.data } : next.data,
        failed: retrying ? { ...baseFailed, ...next.failed } : next.failed,
        error: null, activeColumns, progress: { completed: next.completed, total: next.count },
      });
    });
    if (!res || !profReq.current.isCurrent(req)) return;
    if (res.error) {
      commitProfile({
        status: 'failed', total: retrying ? base.total : null,
        data: retrying ? base.data : {}, failed: retrying ? base.failed : {},
        error: res.error, activeColumns: [], progress: null,
      });
      return;
    }
    commitProfile({
      status: 'results', total: res.total,
      data: retrying ? { ...baseData, ...res.data } : res.data,
      failed: retrying ? { ...baseFailed, ...res.failed } : res.failed,
      error: null, activeColumns: [], progress: null,
    });
  };
  const hideProfile = () => { profReq.current.cancel(); setProfileDetails(false); commitProfile(idleProfile()); };

  if (columns.length === 0 && !connected) return null;

  // Open the per-column ops menu from a header trigger (⌄ click or right-click); anchor under the header cell.
  const openMenu = (ci: number, anchor: HTMLElement) => {
    const rect = anchor.getBoundingClientRect();
    setMenu((m) => (m?.ci === ci ? null : { ci, x: rect.left, y: rect.bottom + 4 }));
  };

  // Profiling docks under each header. Completed columns appear as they arrive; a failed column shows n/a and its
  // reason in place. Columns beyond the disclosed limit remain explicitly not profiled.
  const profileCell = (ci: number): React.ReactNode => {
    const c = columns[ci]; if (!c) return null;
    const reason = profile.failed[c.name];
    if (reason) return (
      <div style={{ ...hint, color: 'var(--sem-bad)' }}>
        <div className="font-semibold">n/a</div>
        <div className="truncate" title={reason}>{reason}</div>
      </div>
    );
    const p = profile.data[c.name];
    if (!p) {
      if (profile.status === 'running' && profile.activeColumns.includes(c.name)) return (
        <div className="animate-pulse" role="status" style={hint}>
          <div className="rounded" style={{ width: '72%', height: 8, background: 'var(--sem-border)' }} />
          <div className="mt-1 rounded" style={{ width: '48%', height: 5, background: 'var(--sem-border)' }} />
          <span className="sr-only">Profiling {c.name}</span>
        </div>
      );
      return <span style={hint}>{ci >= PROFILE_LIMIT ? 'Not profiled' : 'No result'}</span>;
    }
    const total = profile.total ?? 0;
    const validPct = total > 0 ? Math.max(0, Math.min(100, ((total - p.nulls) / total) * 100)) : 100;
    const blankPct = total > 0 ? Math.max(0, Math.min(100, (p.nulls / total) * 100)) : null;
    return (
      <div>
        <div className="flex items-center justify-between text-[10px]" style={{ color: 'var(--sem-muted)' }}>
          <span>{p.distinct.toLocaleString()} distinct</span>
          <span>{p.nulls.toLocaleString()} blank{blankPct != null ? ` (${blankPct.toFixed(0)}%)` : ''}</span>
        </div>
        <div className="mt-0.5 rounded-full overflow-hidden" style={{ height: 4, background: 'color-mix(in srgb,var(--sem-bad) 55%, transparent)' }} title={`${validPct.toFixed(0)}% non-null`}>
          <div style={{ width: validPct + '%', height: '100%', background: 'var(--sem-good)' }} />
        </div>
        {(p.min != null || p.max != null) && (
          <div className="mt-0.5 text-[10px]" style={{ color: 'var(--sem-muted)' }}>
            {p.min != null ? `min ${p.min.toLocaleString()}` : ''}{p.min != null && p.max != null ? ' · ' : ''}{p.max != null ? `max ${p.max.toLocaleString()}` : ''}
          </div>
        )}
      </div>
    );
  };
  const showProfile = profile.status !== 'idle';
  const profiledCount = columns.filter((c) => !!profile.data[c.name]).length;
  const failedCount = columns.filter((c) => !!profile.failed[c.name]).length;
  const notProfiledCount = Math.max(0, columns.length - profiledCount - failedCount);
  const profileActionLabel = profile.status === 'running'
    ? 'Profiling…'
    : profile.status === 'results'
      ? 'Hide profile'
      : profile.status === 'stale'
        ? 'Refresh profile'
        : profile.status === 'failed'
          ? 'Retry profile'
          : 'Profile columns';
  const profileAction = () => profile.status === 'results' ? hideProfile() : void startProfile();
  const profileHasDetails = !!profile.error || failedCount > 0;
  const removeDuplicatesError = transformErrorForAction(transformErrors, tableName, undefined, 'remove-duplicates');
  const keepTopError = transformErrorForAction(transformErrors, tableName, undefined, 'keep-top');
  const transformBusyReason = transformBusy ? `Other transform controls are unavailable while ${transformBusy.label.replace(/…$/, '').toLowerCase()}.` : null;

  return (
    <div className="rounded-lg border" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      {/* table-level transform bar — the per-column ops now live on the grid headers below */}
      <div className="flex items-center gap-2 px-3 py-1.5 flex-wrap" style={{ borderBottom: '1px solid var(--sem-border)' }}>
        <span className="font-semibold text-[11.5px]">Preview</span>
        <span style={{ width: 1, height: 14, background: 'var(--sem-border)' }} />
        <button disabled={!!transformBusy} style={btn} title="Add a Table.Distinct step (all columns)"
          onClick={() => void run(gen.removeDuplicates(mText, []), 'remove-duplicates', 'Removing duplicates…')}>
          {transformBusy?.action === 'remove-duplicates' ? transformBusy.label : 'Remove duplicates'}
        </button>
        {removeDuplicatesError && <span role="alert" style={{ ...hint, color: 'var(--sem-bad)', maxWidth: 420 }}>{removeDuplicatesError}</span>}
        <KeepTopN disabled={!!transformBusy} busy={transformBusy?.action === 'keep-top'} busyLabel={transformBusy?.action === 'keep-top' ? transformBusy.label : undefined}
          onApply={(n) => run(gen.keepTopN(mText, n), 'keep-top', 'Applying…')} />
        {keepTopError && <span role="alert" style={{ ...hint, color: 'var(--sem-bad)', maxWidth: 420 }}>{keepTopError}</span>}
        <span className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>· click a header ⌄ (or right-click) for per-column operations</span>
        {transformBusyReason && <span role="status" style={hint}>{transformBusyReason}</span>}
        <div className="ml-auto flex items-center gap-2">
          {sampleErr && <span role="alert" style={{ ...hint, color: 'var(--sem-bad)', maxWidth: 360 }}>{sampleErr}</span>}
          {connected && <button style={btn} disabled={!!sampleBusy} onClick={() => void loadSample(true)}>{sampleBusy ? 'Refreshing…' : 'Refresh'}</button>}
          <span style={hint}>Profiles up to {PROFILE_LIMIT} columns</span>
          {profile.status === 'stale' && <span role="status" style={{ ...hint, color: 'var(--sem-warn)', fontWeight: 600 }}>Out of date</span>}
          <button style={profile.status === 'idle' || profile.status === 'stale' || profile.status === 'failed' ? primaryBtn : btn}
            disabled={!connected || profile.status === 'running' || visible.length === 0} onClick={profileAction}>
            {profileActionLabel}
          </button>
          {(profile.status === 'stale' || profile.status === 'failed') && <button style={btn} onClick={hideProfile}>Hide</button>}
        </div>
      </div>

      {(!connected || visible.length === 0 || profile.status !== 'results') && (
        <div className="flex items-center gap-2 px-3 py-1.5 flex-wrap" style={{ ...hint, borderBottom: '1px solid var(--sem-border)' }}>
          {!connected ? <span>Profile needs a live connection</span> : visible.length === 0 ? (
            <span>No columns are available to profile.</span>
          ) : profile.status === 'idle' ? (
            <span>Profile columns to see distinct values, blank values, and numeric ranges.</span>
          ) : profile.status === 'running' ? (
            <>
              <div role="progressbar" aria-label="Column profile progress"
                aria-valuemin={profile.progress ? 0 : undefined} aria-valuemax={profile.progress?.total} aria-valuenow={profile.progress?.completed}
                className={profile.progress ? 'overflow-hidden rounded-full' : 'overflow-hidden rounded-full animate-pulse'}
                style={{ width: 120, height: 5, background: 'var(--sem-border)' }}>
                <div style={{
                  height: '100%', background: 'var(--sem-accent)',
                  width: profile.progress && profile.progress.total > 0 ? `${(profile.progress.completed / profile.progress.total) * 100}%` : '35%',
                }} />
              </div>
              <span role="status">{profile.progress ? `${profile.progress.completed} of ${profile.progress.total} columns complete` : 'Preparing profile…'}</span>
            </>
          ) : profile.status === 'stale' ? <span>Results are out of date.</span> : profile.status === 'failed' ? <span>Profile failed.</span> : null}
          {profile.error && <span role="alert" style={{ color: 'var(--sem-bad)', maxWidth: 460 }}>{profile.error}</span>}
        </div>
      )}

      {/* the preview grid — its headers ARE the column-op surface (type glyph + name + ⌄/right-click menu),
          with the profiling strip docked under the headers when Profile is on. Offline: headers only. */}
      <div className="px-2 pt-2 pb-1">
        <ResultGrid
          columns={columns} rows={sample?.rows ?? []} height={showProfile ? 300 : 260} filterable={false}
          showTypeGlyph onColumnMenu={openMenu} menuCol={menu?.ci ?? null}
          subHeader={showProfile ? profileCell : undefined}
        />
      </div>
      {profileDetails && profileHasDetails && (
        <div ref={profileDetailsRef} tabIndex={-1} role="region" aria-label="Profile failure details"
          className="mx-3 mb-2 rounded border px-3 py-2 outline-none" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', fontSize: 10.5 }}>
          <div className="font-semibold mb-1">Profile details</div>
          {profile.error && <div style={{ color: 'var(--sem-bad)' }}>{profile.error}</div>}
          {Object.entries(profile.failed).map(([name, reason]) => (
            <div key={name} className="grid gap-2 py-0.5" style={{ gridTemplateColumns: 'minmax(90px, 180px) minmax(0, 1fr)' }}>
              <span className="font-semibold truncate" title={name}>{name}</span>
              <span style={{ color: 'var(--sem-bad)' }}>{reason}</span>
            </div>
          ))}
          {failedCount > 0 && profile.status === 'results' && (
            <button className="mt-2" style={btn} disabled={!connected}
              onClick={() => void startProfile(Object.keys(profile.failed))}>Retry skipped columns</button>
          )}
        </div>
      )}
      <div className="flex items-center gap-2 px-3 pb-2 flex-wrap" style={{ ...hint }}>
        <span>
          {!connected
            ? 'Connect to a live model to preview rows. Column operations work offline; the preview shows loaded data.'
            : sampleErr
              ? 'Preview unavailable.'
              : sample
                ? `${showProfile && profile.total != null
                  ? `${sample.rowCount.toLocaleString()} of ${profile.total.toLocaleString()} total rows`
                  : `${sample.rowCount.toLocaleString()} preview rows`} · ${sample.columns.length} cols · ${sample.elapsedMs} ms`
                : sampleBusy ? 'Refreshing preview…' : 'No preview loaded.'}
        </span>
        {showProfile && profile.status !== 'running' && (
          <div className="ml-auto flex items-center gap-2 flex-wrap">
            <span>{profiledCount} profiled</span>
            <span>{failedCount} failed</span>
            <span>{notProfiledCount} not profiled{columns.length > PROFILE_LIMIT ? ` (${PROFILE_LIMIT}-column limit)` : ''}</span>
            {profile.status === 'stale' && <span style={{ color: 'var(--sem-warn)' }}>Out of date</span>}
            {profileHasDetails && (
              <button style={linkBtn} onClick={() => setProfileDetails((open) => !open)}>
                {profileDetails ? 'Hide details' : 'View details'}
              </button>
            )}
          </div>
        )}
      </div>

      {menu && (
        <ColumnMenu
          col={columns[menu.ci]} x={menu.x} y={menu.y} mText={mText}
          tableName={tableName} onClose={() => setMenu(null)} run={run} busy={transformBusy} errors={transformErrors}
        />
      )}
    </div>
  );
}

// The per-column dropdown: direct actions + inline sub-forms for the ones that need input. Anchored (fixed)
// under the chip's ⌄, with a click-away backdrop (matches grid.tsx's FilterPopover pattern).
function ColumnMenu({ col, x, y, mText, tableName, onClose, run, busy, errors }: {
  col: GridCol; x: number; y: number; mText: string; tableName: string | null;
  onClose: () => void;
  run: (p: Promise<TransformResult>, action: TransformAction, label: string, column?: string) => Promise<boolean>;
  busy: TransformBusy | null;
  errors: Record<string, string>;
}) {
  const [view, setView] = useState<'menu' | 'rename' | 'type' | 'filter' | 'replace'>('menu');
  const numeric = isNumType(col.type);
  // M operates on the PARTITION-OUTPUT name (SourceColumn), not the model name. Unknown ⇒ the ops stand down —
  // never write M against a guessed name. The sub-views are only reachable from the ops list, so srcName is
  // known inside them.
  const srcName = col.mName;
  const errorFor = (action: TransformAction) => transformErrorForAction(errors, tableName, col.name, action);
  const W = 240;
  const left = Math.max(8, Math.min(x, (typeof window !== 'undefined' ? window.innerWidth : 1200) - W - 8));

  return (
    <>
      <div className="fixed inset-0" style={{ zIndex: 40 }} onClick={() => { if (!busy) onClose(); }} />
      <div className="fixed rounded-lg border shadow-lg" style={{ zIndex: 50, top: y, left, width: W, background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }} onClick={(e) => e.stopPropagation()}>
        <div className="px-3 py-1.5 text-[11px] font-semibold truncate" style={{ borderBottom: '1px solid var(--sem-border)' }} title={col.name}>{col.name}</div>
        {view === 'menu' && srcName == null && (
          <div className="px-3 py-2" style={{ ...hint }}>
            {col.calc
              ? 'This column is calculated in the model; M transforms do not apply.'
              : "M transforms need the column's source name; not available for this column."}
          </div>
        )}
        {view === 'menu' && srcName != null && (
          <div className="py-1">
            <MenuItem disabled={!!busy} busy={busy?.action === 'remove-column'} busyLabel={busy?.label}
              onClick={() => void run(gen.removeColumns(mText, [srcName]), 'remove-column', 'Removing…', col.name)}>Remove</MenuItem>
            <TransformActionError error={errorFor('remove-column')} />
            <MenuItem disabled={!!busy} onClick={() => setView('rename')}>Rename…</MenuItem>
            <MenuItem disabled={!!busy} onClick={() => setView('type')}>Change type…</MenuItem>
            <MenuItem disabled={!!busy} onClick={() => setView('filter')}>Filter rows…</MenuItem>
            <MenuItem disabled={!!busy} onClick={() => setView('replace')}>Replace values…</MenuItem>
            <MenuItem disabled={!!busy} busy={busy?.action === 'sort-ascending'} busyLabel={busy?.label}
              onClick={() => void run(gen.sort(mText, srcName, false), 'sort-ascending', 'Sorting…', col.name)}>Sort ascending</MenuItem>
            <TransformActionError error={errorFor('sort-ascending')} />
            <MenuItem disabled={!!busy} busy={busy?.action === 'sort-descending'} busyLabel={busy?.label}
              onClick={() => void run(gen.sort(mText, srcName, true), 'sort-descending', 'Sorting…', col.name)}>Sort descending</MenuItem>
            <TransformActionError error={errorFor('sort-descending')} />
            <MenuItem disabled={!!busy} busy={busy?.action === 'trim-clean'} busyLabel={busy?.label}
              onClick={() => void run(gen.trimClean(mText, [srcName]), 'trim-clean', 'Cleaning…', col.name)}>Trim &amp; Clean</MenuItem>
            <TransformActionError error={errorFor('trim-clean')} />
            {busy && <div className="px-3 py-1" role="status" style={hint}>Other actions are unavailable while {busy.label.replace(/…$/, '').toLowerCase()}.</div>}
          </div>
        )}
        {view === 'rename' && srcName != null && (
          <RenameForm onCancel={() => setView('menu')} onApply={(to) => run(gen.renameColumn(mText, srcName, to), 'rename-column', 'Renaming…', col.name)}
            initial={srcName} busy={!!busy} busyLabel={busy?.label} error={errorFor('rename-column')} />
        )}
        {view === 'type' && srcName != null && (
          <TypeForm onCancel={() => setView('menu')} onApply={(mType) => run(gen.changeType(mText, srcName, mType), 'change-type', 'Changing type…', col.name)}
            busy={!!busy} busyLabel={busy?.label} error={errorFor('change-type')} />
        )}
        {view === 'filter' && srcName != null && (
          <FilterForm numeric={numeric} onCancel={() => setView('menu')}
            onApply={(op, literal) => run(gen.filterRows(mText, srcName, op, literal), 'filter-rows', 'Filtering…', col.name)}
            busy={!!busy} busyLabel={busy?.label} error={errorFor('filter-rows')} />
        )}
        {view === 'replace' && srcName != null && (
          <ReplaceForm onCancel={() => setView('menu')} onApply={(find, repl) => run(gen.replaceValues(mText, srcName, find, repl), 'replace-values', 'Replacing…', col.name)}
            busy={!!busy} busyLabel={busy?.label} error={errorFor('replace-values')} />
        )}
      </div>
    </>
  );
}

function TransformActionError({ error }: { error: string | null }) {
  return error ? <div className="px-3 py-1" role="alert" style={{ ...hint, color: 'var(--sem-bad)' }}>{error}</div> : null;
}

function MenuItem({ children, onClick, disabled, busy, busyLabel }: { children: React.ReactNode; onClick: () => void; disabled?: boolean; busy?: boolean; busyLabel?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} className="w-full text-left px-3 py-1 text-[11.5px] hover:opacity-80"
      style={{ background: 'transparent', border: 'none', color: 'var(--sem-fg)', cursor: disabled ? 'default' : 'pointer' }}>{busy ? (busyLabel ?? 'Applying…') : children}</button>
  );
}

function SubForm({ children, onApply, onCancel, canApply = true, applyLabel = 'Add step', busy, busyLabel, error }: {
  children: React.ReactNode;
  onApply: () => Promise<boolean>;
  onCancel: () => void;
  canApply?: boolean;
  applyLabel?: string;
  busy?: boolean;
  busyLabel?: string;
  error?: string | null;
}) {
  return (
    <div className="p-2.5 flex flex-col gap-2">
      {children}
      {error && <div role="alert" style={{ ...hint, color: 'var(--sem-bad)' }}>{error}</div>}
      <div className="flex items-center gap-2 justify-end pt-1">
        <button onClick={onCancel} disabled={busy} style={btn}>Cancel</button>
        <button onClick={() => void onApply()} disabled={busy || !canApply} style={primaryBtn}>{busy ? (busyLabel ?? 'Applying…') : applyLabel}</button>
      </div>
    </div>
  );
}

function RenameForm({ initial, onApply, onCancel, busy, busyLabel, error }: { initial: string; onApply: (to: string) => Promise<boolean>; onCancel: () => void; busy?: boolean; busyLabel?: string; error?: string | null }) {
  const [v, setV] = useState(initial);
  return (
    <SubForm onCancel={onCancel} onApply={() => onApply(v.trim())} canApply={!!v.trim() && v.trim() !== initial} busy={busy} busyLabel={busyLabel} error={error}>
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>New column name</label>
      <input autoFocus value={v} disabled={busy} onChange={(e) => setV(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter' && v.trim() && !busy) void onApply(v.trim()); }} style={{ ...input, maxWidth: 'unset', width: '100%' }} />
    </SubForm>
  );
}

function TypeForm({ onApply, onCancel, busy, busyLabel, error }: { onApply: (mType: string) => Promise<boolean>; onCancel: () => void; busy?: boolean; busyLabel?: string; error?: string | null }) {
  const [t, setT] = useState(M_TYPES[0][1]);
  return (
    <SubForm onCancel={onCancel} onApply={() => onApply(t)} busy={busy} busyLabel={busyLabel} error={error}>
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Change type to</label>
      <select autoFocus value={t} disabled={busy} onChange={(e) => setT(e.target.value)} style={{ ...input, maxWidth: 'unset', width: '100%' }}>
        {M_TYPES.map(([label, mType]) => <option key={mType} value={mType}>{label}</option>)}
      </select>
    </SubForm>
  );
}

function FilterForm({ numeric, onApply, onCancel, busy, busyLabel, error }: { numeric: boolean; onApply: (op: FilterOp, literal: string) => Promise<boolean>; onCancel: () => void; busy?: boolean; busyLabel?: string; error?: string | null }) {
  const ops = numeric ? NUM_OPS : TXT_OPS;
  const [op, setOp] = useState<FilterOp>(ops[0][0]);
  const [val, setVal] = useState('');
  const needsValue = op !== 'nonEmpty';
  // numeric columns emit a RAW numeric literal (so the filter stays numeric); text uses an M string literal.
  const literal = () => {
    if (!needsValue) return '';
    const t = val.trim();
    return numeric && /^-?\d+(\.\d+)?$/.test(t) ? t : mString(val);
  };
  return (
    <SubForm onCancel={onCancel} onApply={() => onApply(op, literal())} canApply={!needsValue || val.trim().length > 0} applyLabel="Filter" busy={busy} busyLabel={busyLabel} error={error}>
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Keep rows where the value…</label>
      <select autoFocus value={op} disabled={busy} onChange={(e) => setOp(e.target.value as FilterOp)} style={{ ...input, maxWidth: 'unset', width: '100%' }}>
        {ops.map(([o, label]) => <option key={o} value={o}>{label}</option>)}
      </select>
      {needsValue && (
        <input value={val} disabled={busy} onChange={(e) => setVal(e.target.value)} placeholder={numeric ? 'value (number)' : 'value'} inputMode={numeric ? 'decimal' : 'text'}
          onKeyDown={(e) => { if (e.key === 'Enter' && val.trim() && !busy) void onApply(op, literal()); }} style={{ ...input, maxWidth: 'unset', width: '100%' }} />
      )}
    </SubForm>
  );
}

function ReplaceForm({ onApply, onCancel, busy, busyLabel, error }: { onApply: (find: string, repl: string) => Promise<boolean>; onCancel: () => void; busy?: boolean; busyLabel?: string; error?: string | null }) {
  const [find, setFind] = useState('');
  const [repl, setRepl] = useState('');
  return (
    <SubForm onCancel={onCancel} onApply={() => onApply(find, repl)} canApply={find.length > 0} busy={busy} busyLabel={busyLabel} error={error}>
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Value to find</label>
      <input autoFocus value={find} disabled={busy} onChange={(e) => setFind(e.target.value)} style={{ ...input, maxWidth: 'unset', width: '100%' }} />
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Replace with</label>
      <input value={repl} disabled={busy} onChange={(e) => setRepl(e.target.value)} style={{ ...input, maxWidth: 'unset', width: '100%' }} />
    </SubForm>
  );
}

function KeepTopN({ disabled, busy, busyLabel, onApply }: { disabled?: boolean; busy?: boolean; busyLabel?: string; onApply: (n: number) => Promise<boolean> }) {
  const [open, setOpen] = useState(false);
  const [n, setN] = useState(100);
  if (!open) return <button disabled={disabled} style={btn} onClick={() => setOpen(true)} title="Keep the first N rows (Table.FirstN)">Keep top N…</button>;
  const apply = async () => { if (await onApply(n)) setOpen(false); };
  return (
    <span className="inline-flex items-center gap-1">
      <input type="number" min={1} autoFocus value={n} disabled={busy} onChange={(e) => setN(Math.max(1, parseInt(e.target.value || '1', 10)))}
        onKeyDown={(e) => { if (e.key === 'Enter' && !busy) void apply(); else if (e.key === 'Escape' && !busy) setOpen(false); }} style={{ ...input, width: 64 }} />
      <button style={primaryBtn} disabled={busy} onClick={() => void apply()}>{busy ? (busyLabel ?? 'Applying…') : 'Keep'}</button>
      <button style={btn} disabled={busy} onClick={() => setOpen(false)}>✕</button>
    </span>
  );
}

// Standalone row-count probe — its own query so no single bad column can hide the total (DISTINCTCOUNT counts a
// blank as one value; the caveat says "profiled from loaded data").
function profileRowsDax(table: string): string {
  return `EVALUATE ROW("rows", COUNTROWS('${table.replace(/'/g, "''")}'))`;
}

// One chunk of the column profile: DISTINCTCOUNT/COUNTBLANK (+ numeric MIN/MAX) per column, each IFERROR-guarded so
// an eval-time error degrades to the PROFILE_ERR sentinel rather than nuking the row. A whole-chunk hard-fail
// (bind-time reject, which IFERROR can't catch) is bisected to solo probes by the caller.
function profileChunkDax(table: string, cols: { name: string; i: number; numeric: boolean }[]): string {
  const t = `'${table.replace(/'/g, "''")}'`;
  const parts: string[] = [];
  cols.forEach(({ name, i, numeric }) => {
    const ref = `${t}[${name.replace(/\]/g, ']]')}]`;
    parts.push(`"d${i}", IFERROR(DISTINCTCOUNT(${ref}), ${PROFILE_ERR})`);
    parts.push(`"b${i}", IFERROR(COUNTBLANK(${ref}), ${PROFILE_ERR})`);
    if (numeric) {
      parts.push(`"mn${i}", IFERROR(MIN(${ref}), BLANK())`);
      parts.push(`"mx${i}", IFERROR(MAX(${ref}), BLANK())`);
    }
  });
  return `EVALUATE\nROW(\n  ${parts.join(',\n  ')}\n)`;
}

// ---- interactive Applied Steps ------------------------------------------------------------------------

/**
 * The Applied-Steps outline, upgraded to interactive: hover a step for ✎ rename (inline) and ✕ delete
 * (kernel re-points references to the predecessor; first/only-step refusals are the kernel's designed
 * messages, shown verbatim). Clicking a step selects/scrolls its binding span in the editor. Falls back to
 * a read-only list when the M isn't a single let…in… shape (letShape returns null).
 */
export function AppliedStepsPanel({ text, contextToken, isContextCurrent, onChange, onSelect, onError, collapsibleOnCompact = false }: {
  text: string;
  contextToken: string;
  isContextCurrent: (captured: string) => boolean;
  onChange: (m: string) => void;
  onSelect: (from: number, to: number) => void;
  onError: (msg: string) => void;
  collapsibleOnCompact?: boolean;
}) {
  const shape = useMemo(() => letShape(text), [text]);
  const [editing, setEditing] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  type StepOperation = { resource: string; action: 'rename' | 'delete' };
  type StepBusy = StepOperation & { ownerId: number; contextToken: string };
  const [busy, setBusy] = useState<StepBusy | null>(null);
  const busyGate = useRef(createContextBusyOwnerGate<StepOperation>(contextToken));
  const [stepError, setStepError] = useState<{ name: string; text: string } | null>(null);
  const [compactOpen, setCompactOpen] = useState(false);
  useEffect(() => {
    busyGate.current.resetContext(contextToken);
    setBusy(null); setEditing(null); setDraft(''); setStepError(null);
  }, [contextToken]);
  useEffect(() => () => { busyGate.current.reset(); }, []);

  const doRename = async (oldName: string) => {
    const to = draft.trim();
    if (!to || to === oldName) { setEditing(null); return; }
    const operationContext = contextToken;
    const next = { resource: `step:${oldName}`, action: 'rename' as const };
    const owner = busyGate.current.begin(next, operationContext);
    if (!owner) return;
    setBusy(owner); setStepError(null);
    try {
      const r = await renameStep(text, oldName, to);
      if (!busyGate.current.isOwner(owner)) return;
      if (!isContextCurrent(operationContext)) {
        onError('Rename result discarded because the table, query, or M text changed while it was running.');
        return;
      }
      if (r.ok) { onChange(r.m); setEditing(null); }
      else { setStepError({ name: oldName, text: r.error }); onError(r.error); }
    } catch (e) {
      if (!busyGate.current.isOwner(owner)) return;
      if (!isContextCurrent(operationContext)) {
        onError('Rename result discarded because the table, query, or M text changed while it was running.');
        return;
      }
      const error = String((e as Error).message ?? e);
      setStepError({ name: oldName, text: error }); onError(error);
    } finally { if (busyGate.current.release(owner)) setBusy(null); }
  };
  const doDelete = async (name: string) => {
    const operationContext = contextToken;
    const next = { resource: `step:${name}`, action: 'delete' as const };
    const owner = busyGate.current.begin(next, operationContext);
    if (!owner) return;
    setBusy(owner); setStepError(null);
    try {
      const r = await deleteStep(text, name);
      if (!busyGate.current.isOwner(owner)) return;
      if (!isContextCurrent(operationContext)) {
        onError('Delete result discarded because the table, query, or M text changed while it was running.');
        return;
      }
      if (r.ok) onChange(r.m);
      else { setStepError({ name, text: r.error }); onError(r.error); }
    } catch (e) {
      if (!busyGate.current.isOwner(owner)) return;
      if (!isContextCurrent(operationContext)) {
        onError('Delete result discarded because the table, query, or M text changed while it was running.');
        return;
      }
      const error = String((e as Error).message ?? e);
      setStepError({ name, text: error }); onError(error);
    } finally { if (busyGate.current.release(owner)) setBusy(null); }
  };

  return (
    <div className="rounded-lg border" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="flex items-center gap-2 px-3 py-2 text-[12px] font-semibold" style={{ borderBottom: '1px solid var(--sem-border)' }}>
        <span>Applied steps{shape ? ` (${shape.steps.length})` : ''}</span>
        {collapsibleOnCompact && (
          <button className="ml-auto xl:hidden" onClick={() => setCompactOpen((open) => !open)} aria-expanded={compactOpen}
            style={{ ...btn, padding: '1px 7px' }}>{compactOpen ? 'Collapse ▴' : 'Expand ▾'}</button>
        )}
      </div>
      <div className={collapsibleOnCompact && !compactOpen ? 'hidden xl:block' : ''}>
        <div className="px-2 py-2" style={{ maxHeight: 420, overflow: 'auto' }}>
          {!shape ? (
            <div style={{ ...hint, padding: '2px 6px' }}>No let…in… steps detected. Edit the M directly.</div>
          ) : (
            <>
              {shape.steps.length === 0 && <div style={{ ...hint, padding: '2px 6px' }}>No steps detected.</div>}
              {shape.steps.map((st, i) => {
                const thisBusy = busy?.resource === `step:${st.name}` ? busy.action : null;
                return (
                  <div key={st.name}>
                    <div className="group flex items-center gap-1 px-2 py-1 rounded-md hover:bg-[color-mix(in_srgb,var(--sem-fg)_6%,transparent)]" style={{ fontSize: 11 }}>
                      <span style={{ color: 'var(--sem-muted)', width: 16 }}>{i + 1}</span>
                      {editing === st.name ? (
                        <>
                          <input autoFocus value={draft} disabled={!!busy} onChange={(e) => setDraft(e.target.value)}
                            onKeyDown={(e) => { if (e.key === 'Enter') void doRename(st.name); else if (e.key === 'Escape' && !busy) setEditing(null); }}
                            onBlur={() => void doRename(st.name)} style={{ ...input, maxWidth: 'unset', flex: 1, minWidth: 0 }} />
                          {thisBusy === 'rename' && <span style={hint}>Renaming…</span>}
                        </>
                      ) : (
                        <button onClick={() => onSelect(st.start, st.end)} title="Select this step in the editor" className="flex-1 min-w-0 truncate text-left"
                          style={{ background: 'none', border: 'none', color: 'var(--sem-fg)', cursor: 'pointer', padding: 0 }}>{st.name}</button>
                      )}
                      {editing !== st.name && (
                        <span className={`shrink-0 flex items-center gap-0.5 transition-opacity ${thisBusy ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'}`}>
                          <button onClick={() => { setEditing(st.name); setDraft(st.name); setStepError(null); }} disabled={!!busy} title="Rename step"
                            style={{ background: 'none', border: 'none', color: 'var(--sem-muted)', cursor: busy ? 'default' : 'pointer', padding: '0 2px' }}>✎</button>
                          <button onClick={() => void doDelete(st.name)} disabled={!!busy} title="Delete step (references re-point to the previous step)"
                            style={{ background: 'none', border: 'none', color: 'var(--sem-bad)', cursor: busy ? 'default' : 'pointer', padding: '0 2px' }}>{thisBusy === 'delete' ? 'Deleting…' : '✕'}</button>
                        </span>
                      )}
                    </div>
                    {stepError?.name === st.name && <div className="px-8 pb-1" role="alert" style={{ ...hint, color: 'var(--sem-bad)' }}>{stepError.text}</div>}
                  </div>
                );
              })}
              <div className="px-2 py-1 truncate" style={{ fontSize: 11 }} title={shape.result}>
                <span style={{ color: 'var(--sem-muted)', marginRight: 6 }}>→</span>
                <span style={{ color: 'var(--sem-accent)' }}>(result)</span>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
