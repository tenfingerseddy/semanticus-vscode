import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { rpc } from './bridge';
import type { ResultSet } from './wire';
import { ResultGrid } from './grid';
import { gen, mString, letShape, renameStep, deleteStep, M_TYPES, type TransformResult } from './mtransform';

// ===================================================================================================
// M Code tab — the "UI writes M for you" surface (docs/pq-transforms-plan.md §1–3). Everything here
// is deterministic text transformation over the editor's M (the [F] kernel in mtransform.ts) plus ONE
// read-only DAX profile against the LOADED table. There is no cross-platform Mashup engine, so we never
// evaluate M per-step: generated steps take effect at the NEXT refresh; the sample shows loaded data.
//   • SamplePreview — the loaded-data sample grid whose column HEADERS are the interaction surface: a type
//     glyph + name, a ⌄ / right-click per-column ops menu, and (when Profile is on) a docked profiling row.
//     A slim table-level transform bar (remove duplicates / keep top N + the Profile toggle) sits on top.
//   • AppliedStepsPanel — the interactive Applied-Steps outline (rename / delete / click-to-select).
// ===================================================================================================

// shared inline styles (mirrors mcode.tsx — kept local so this module stands alone)
const input: React.CSSProperties = { background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, fontSize: 11, padding: '2px 6px', maxWidth: 220 };
const btn: React.CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, fontSize: 11, padding: '3px 10px', cursor: 'pointer' };
const primaryBtn: React.CSSProperties = { ...btn, background: 'var(--sem-accent-soft)', borderColor: 'var(--sem-accent)' };
const hint: React.CSSProperties = { color: 'var(--sem-muted)', fontSize: 10.5 };

export interface GridCol { name: string; type?: string }

const isNumType = (t?: string) => !!t && /int|dec|doub|number|curr|float|real|money/i.test(t);

// ---- transform bar + per-column menu + profiling strip ------------------------------------------------

type FilterOp = 'equals' | 'notEquals' | 'contains' | 'beginsWith' | 'greater' | 'less' | 'nonEmpty';
const NUM_OPS: [FilterOp, string][] = [['equals', '='], ['notEquals', '≠'], ['greater', '>'], ['less', '<'], ['nonEmpty', 'is not empty']];
const TXT_OPS: [FilterOp, string][] = [['equals', 'equals'], ['notEquals', 'does not equal'], ['contains', 'contains'], ['beginsWith', 'begins with'], ['nonEmpty', 'is not empty']];

interface ProfileCell { distinct: number; nulls: number }
interface ProfileState { loading: boolean; total: number | null; data: Record<string, ProfileCell> | null; error: string | null }

/**
 * The loaded-data sample and its transform surface, bundled into ONE block. A slim table-level bar (remove
 * duplicates / keep top N + Profile toggle + Refresh) sits above the preview grid; the grid's COLUMN HEADERS
 * are the per-column op surface — a type glyph + name, and a ⌄ / right-click menu offering the same ops the
 * kernel already implements (rename / change type / remove / filter / replace / sort / trim). When Profile is
 * on, a distinct/null strip docks directly under the headers, column-aligned. Column names come from the loaded
 * sample when present, else the doc model's columns for the table — so the headers (and every op) work OFFLINE;
 * only the row data + profiling need a live connection. Each op runs a kernel generator against the CURRENT
 * editor text and, on ok, replaces it (dirty, exactly like a manual edit — Save stays the explicit act) with a
 * toast; on ok:false the kernel's error is shown verbatim.
 */
export function SamplePreview({ tableName, docColumns, mText, connected, apply, fail }: {
  tableName: string | null;
  docColumns: GridCol[];                                 // offline fallback columns (doc model) for the selected table
  mText: string;
  connected: boolean;
  apply: (m: string, stepName: string) => void;          // ok  → replace editor text + toast
  fail: (error: string) => void;                         // err → show the kernel message verbatim
}) {
  const [menu, setMenu] = useState<{ ci: number; x: number; y: number } | null>(null);
  const [profileOn, setProfileOn] = useState(false);
  const [profile, setProfile] = useState<ProfileState>({ loading: false, total: null, data: null, error: null });
  const [tableBusy, setTableBusy] = useState(false);
  const profReq = useRef(0);

  // The live sample (read-only EVALUATE TOPN of the loaded table — not a refresh; no per-step M eval exists
  // cross-platform). Owned here so the sample + its transform headers are one surface. A request token guards
  // against a stale table's result landing after a switch.
  const [sample, setSample] = useState<ResultSet | null>(null);
  const [sampleErr, setSampleErr] = useState<string | null>(null);
  const [sampleBusy, setSampleBusy] = useState(false);
  const sampleReq = useRef(0);
  const loadSample = useCallback(async () => {
    if (!tableName || !connected) return;
    const req = ++sampleReq.current;
    setSampleBusy(true); setSampleErr(null);
    try {
      const r = await rpc<ResultSet>('previewTable', tableName, 100);
      if (req !== sampleReq.current) return;
      if (r?.error) { setSampleErr(r.error); setSample(null); }
      else if (r) setSample(r);
      else { setSampleErr('No data returned.'); setSample(null); }
    } catch (e) { if (req === sampleReq.current) setSampleErr(String((e as Error).message ?? e)); }
    finally { if (req === sampleReq.current) setSampleBusy(false); }
  }, [tableName, connected]);
  // Reset on table switch and auto-load once when a live engine is present (the Data tab auto-previews the
  // same way) — so the preview headers show live types/rows without a manual step. Offline the headers still
  // render from docColumns.
  useEffect(() => {
    sampleReq.current++; setSample(null); setSampleErr(null); setMenu(null);
    if (connected && tableName) void loadSample();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tableName, connected]);

  // Display columns: the loaded sample's (live types) when present, else the doc-model columns for the table.
  const columns = useMemo<GridCol[]>(
    () => (sample?.columns?.length ? sample.columns.map((c) => ({ name: c.name, type: c.type })) : docColumns),
    [sample, docColumns],
  );

  // run a generator; ok → apply, err → surface verbatim
  const run = async (p: Promise<TransformResult>) => {
    setMenu(null); setTableBusy(true);
    try { const r = await p; if (r.ok) apply(r.m, r.step ?? 'Step'); else fail(r.error); }
    finally { setTableBusy(false); }
  };

  // ONE read-only DAX profile of the loaded table (distinct / nulls per column, ≤12 cols). Failures degrade
  // to a muted note — profiling never breaks the sample. Re-runs when toggled on or the columns change.
  const visible = useMemo(() => columns.slice(0, 12), [columns]);
  useEffect(() => {
    if (!profileOn || !connected || !tableName || visible.length === 0) return;
    const req = ++profReq.current;
    setProfile({ loading: true, total: null, data: null, error: null });
    (async () => {
      try {
        const r = await rpc<ResultSet>('runDax', profileDax(tableName, visible.map((c) => c.name)), 1);
        if (req !== profReq.current) return;
        if (!r || r.error || !r.rows?.length) { setProfile({ loading: false, total: null, data: null, error: r?.error ?? 'no data' }); return; }
        const row = r.rows[0];
        const at = (key: string) => {
          const idx = r.columns.findIndex((c) => c.name.replace(/[[\]]/g, '') === key);
          const v = idx >= 0 ? Number(row[idx]) : NaN;
          return Number.isFinite(v) ? v : 0;
        };
        const data: Record<string, ProfileCell> = {};
        visible.forEach((c, i) => { data[c.name] = { distinct: at('d' + i), nulls: at('b' + i) }; });
        setProfile({ loading: false, total: at('rows'), data, error: null });
      } catch (e) { if (req === profReq.current) setProfile({ loading: false, total: null, data: null, error: String((e as Error).message ?? e) }); }
    })();
  }, [profileOn, connected, tableName, visible]);

  if (columns.length === 0 && !connected) return null;

  // Open the per-column ops menu from a header trigger (⌄ click or right-click); anchor under the header cell.
  const openMenu = (ci: number, anchor: HTMLElement) => {
    const rect = anchor.getBoundingClientRect();
    setMenu((m) => (m?.ci === ci ? null : { ci, x: rect.left, y: rect.bottom + 4 }));
  };

  // Profiling docked under a header (rendered per column by ResultGrid's subHeader). Muted note on the first
  // column when unavailable so the strip is never a wall of "profiling…" cells.
  const profileCell = (ci: number): React.ReactNode => {
    const c = columns[ci]; if (!c) return null;
    const p = profile.data?.[c.name];
    if (profile.loading) return ci === 0 ? <span style={{ ...hint }}>profiling…</span> : null;
    if (profile.error || !p) return ci === 0 ? <span style={{ ...hint }}>profile unavailable</span> : null;
    const total = profile.total ?? 0;
    const validPct = total > 0 ? Math.max(0, Math.min(100, ((total - p.nulls) / total) * 100)) : 100;
    return (
      <div>
        <div className="flex items-center justify-between text-[10px]" style={{ color: 'var(--sem-muted)' }}>
          <span>{p.distinct.toLocaleString()} distinct</span><span>{p.nulls.toLocaleString()} null</span>
        </div>
        <div className="mt-0.5 rounded-full overflow-hidden" style={{ height: 4, background: 'color-mix(in srgb,var(--sem-bad) 55%, transparent)' }} title={`${validPct.toFixed(0)}% non-null`}>
          <div style={{ width: validPct + '%', height: '100%', background: 'var(--sem-good)' }} />
        </div>
      </div>
    );
  };
  const showProfile = profileOn && connected;

  return (
    <div className="rounded-lg border" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      {/* table-level transform bar — the per-column ops now live on the grid headers below */}
      <div className="flex items-center gap-2 px-3 py-1.5 flex-wrap" style={{ borderBottom: '1px solid var(--sem-border)' }}>
        <span className="font-semibold text-[11.5px]">Sample &amp; transforms</span>
        <span style={{ width: 1, height: 14, background: 'var(--sem-border)' }} />
        <button disabled={tableBusy} style={btn} title="Add a Table.Distinct step (all columns)" onClick={() => void run(gen.removeDuplicates(mText, []))}>Remove duplicates</button>
        <KeepTopN disabled={tableBusy} onApply={(n) => void run(gen.keepTopN(mText, n))} />
        <span className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>· click a header ⌄ (or right-click) for per-column operations</span>
        <div className="ml-auto flex items-center gap-2">
          {connected && <button style={btn} disabled={sampleBusy} onClick={() => void loadSample()}>{sampleBusy ? 'Loading…' : 'Refresh'}</button>}
          <label className="flex items-center gap-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }} title="Profile the loaded table: distinct + null counts per column (one read-only DAX query)">
            <input type="checkbox" checked={profileOn} onChange={(e) => setProfileOn(e.target.checked)} disabled={!connected} /> Profile
          </label>
        </div>
      </div>

      {/* the preview grid — its headers ARE the column-op surface (type glyph + name + ⌄/right-click menu),
          with the profiling strip docked under the headers when Profile is on. Offline: headers only. */}
      <div className="px-2 pt-2 pb-1">
        <ResultGrid
          columns={columns} rows={sample?.rows ?? []} height={showProfile ? 300 : 260} filterable={false}
          showTypeGlyph onColumnMenu={openMenu} menuCol={menu?.ci ?? null}
          subHeader={showProfile ? profileCell : undefined}
        />
      </div>
      <div className="flex items-center gap-2 px-3 pb-2" style={{ ...hint }}>
        {!connected ? (
          <span>Connect to a live model to sample rows &amp; profile: a read-only <code>EVALUATE TOPN</code> (not a refresh). Column operations work offline; the sample shows loaded data.</span>
        ) : sampleErr ? (
          <span style={{ color: 'var(--sem-bad)' }}>{sampleErr}</span>
        ) : sample ? (
          <span>{sample.rowCount} rows · {sample.columns.length} cols · {sample.elapsedMs} ms{showProfile && profile.total != null ? ` · profiled ${profile.total.toLocaleString()} rows` : ''}</span>
        ) : (
          <span>{sampleBusy ? 'Loading sample…' : 'No sample loaded.'}</span>
        )}
      </div>

      {menu && (
        <ColumnMenu
          col={columns[menu.ci]} x={menu.x} y={menu.y} mText={mText}
          onClose={() => setMenu(null)} run={run}
        />
      )}
    </div>
  );
}

// The per-column dropdown: direct actions + inline sub-forms for the ones that need input. Anchored (fixed)
// under the chip's ⌄, with a click-away backdrop (matches grid.tsx's FilterPopover pattern).
function ColumnMenu({ col, x, y, mText, onClose, run }: {
  col: GridCol; x: number; y: number; mText: string;
  onClose: () => void; run: (p: Promise<TransformResult>) => Promise<void>;
}) {
  const [view, setView] = useState<'menu' | 'rename' | 'type' | 'filter' | 'replace'>('menu');
  const numeric = isNumType(col.type);
  const W = 240;
  const left = Math.max(8, Math.min(x, (typeof window !== 'undefined' ? window.innerWidth : 1200) - W - 8));

  return (
    <>
      <div className="fixed inset-0" style={{ zIndex: 40 }} onClick={onClose} />
      <div className="fixed rounded-lg border shadow-lg" style={{ zIndex: 50, top: y, left, width: W, background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }} onClick={(e) => e.stopPropagation()}>
        <div className="px-3 py-1.5 text-[11px] font-semibold truncate" style={{ borderBottom: '1px solid var(--sem-border)' }} title={col.name}>{col.name}</div>
        {view === 'menu' && (
          <div className="py-1">
            <MenuItem onClick={() => run(gen.removeColumns(mText, [col.name]))}>Remove</MenuItem>
            <MenuItem onClick={() => setView('rename')}>Rename…</MenuItem>
            <MenuItem onClick={() => setView('type')}>Change type…</MenuItem>
            <MenuItem onClick={() => setView('filter')}>Filter rows…</MenuItem>
            <MenuItem onClick={() => setView('replace')}>Replace values…</MenuItem>
            <MenuItem onClick={() => run(gen.sort(mText, col.name, false))}>Sort ascending</MenuItem>
            <MenuItem onClick={() => run(gen.sort(mText, col.name, true))}>Sort descending</MenuItem>
            <MenuItem onClick={() => run(gen.trimClean(mText, [col.name]))}>Trim &amp; Clean</MenuItem>
          </div>
        )}
        {view === 'rename' && (
          <RenameForm onCancel={() => setView('menu')} onApply={(to) => run(gen.renameColumn(mText, col.name, to))} initial={col.name} />
        )}
        {view === 'type' && (
          <TypeForm onCancel={() => setView('menu')} onApply={(mType) => run(gen.changeType(mText, col.name, mType))} />
        )}
        {view === 'filter' && (
          <FilterForm numeric={numeric} onCancel={() => setView('menu')}
            onApply={(op, literal) => run(gen.filterRows(mText, col.name, op, literal))} />
        )}
        {view === 'replace' && (
          <ReplaceForm onCancel={() => setView('menu')} onApply={(find, repl) => run(gen.replaceValues(mText, col.name, find, repl))} />
        )}
      </div>
    </>
  );
}

function MenuItem({ children, onClick }: { children: React.ReactNode; onClick: () => void }) {
  return (
    <button onClick={onClick} className="w-full text-left px-3 py-1 text-[11.5px] hover:opacity-80"
      style={{ background: 'transparent', border: 'none', color: 'var(--sem-fg)', cursor: 'pointer' }}>{children}</button>
  );
}

function SubForm({ children, onApply, onCancel, canApply = true, applyLabel = 'Add step' }: { children: React.ReactNode; onApply: () => void; onCancel: () => void; canApply?: boolean; applyLabel?: string }) {
  return (
    <div className="p-2.5 flex flex-col gap-2">
      {children}
      <div className="flex items-center gap-2 justify-end pt-1">
        <button onClick={onCancel} style={btn}>Cancel</button>
        <button onClick={onApply} disabled={!canApply} style={primaryBtn}>{applyLabel}</button>
      </div>
    </div>
  );
}

function RenameForm({ initial, onApply, onCancel }: { initial: string; onApply: (to: string) => void; onCancel: () => void }) {
  const [v, setV] = useState(initial);
  return (
    <SubForm onCancel={onCancel} onApply={() => onApply(v.trim())} canApply={!!v.trim() && v.trim() !== initial}>
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>New column name</label>
      <input autoFocus value={v} onChange={(e) => setV(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter' && v.trim()) onApply(v.trim()); }} style={{ ...input, maxWidth: 'unset', width: '100%' }} />
    </SubForm>
  );
}

function TypeForm({ onApply, onCancel }: { onApply: (mType: string) => void; onCancel: () => void }) {
  const [t, setT] = useState(M_TYPES[0][1]);
  return (
    <SubForm onCancel={onCancel} onApply={() => onApply(t)}>
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Change type to</label>
      <select autoFocus value={t} onChange={(e) => setT(e.target.value)} style={{ ...input, maxWidth: 'unset', width: '100%' }}>
        {M_TYPES.map(([label, mType]) => <option key={mType} value={mType}>{label}</option>)}
      </select>
    </SubForm>
  );
}

function FilterForm({ numeric, onApply, onCancel }: { numeric: boolean; onApply: (op: FilterOp, literal: string) => void; onCancel: () => void }) {
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
    <SubForm onCancel={onCancel} onApply={() => onApply(op, literal())} canApply={!needsValue || val.trim().length > 0} applyLabel="Filter">
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Keep rows where the value…</label>
      <select autoFocus value={op} onChange={(e) => setOp(e.target.value as FilterOp)} style={{ ...input, maxWidth: 'unset', width: '100%' }}>
        {ops.map(([o, label]) => <option key={o} value={o}>{label}</option>)}
      </select>
      {needsValue && (
        <input value={val} onChange={(e) => setVal(e.target.value)} placeholder={numeric ? 'value (number)' : 'value'} inputMode={numeric ? 'decimal' : 'text'}
          onKeyDown={(e) => { if (e.key === 'Enter' && val.trim()) onApply(op, literal()); }} style={{ ...input, maxWidth: 'unset', width: '100%' }} />
      )}
    </SubForm>
  );
}

function ReplaceForm({ onApply, onCancel }: { onApply: (find: string, repl: string) => void; onCancel: () => void }) {
  const [find, setFind] = useState('');
  const [repl, setRepl] = useState('');
  return (
    <SubForm onCancel={onCancel} onApply={() => onApply(find, repl)} canApply={find.length > 0}>
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Value to find</label>
      <input autoFocus value={find} onChange={(e) => setFind(e.target.value)} style={{ ...input, maxWidth: 'unset', width: '100%' }} />
      <label className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Replace with</label>
      <input value={repl} onChange={(e) => setRepl(e.target.value)} style={{ ...input, maxWidth: 'unset', width: '100%' }} />
    </SubForm>
  );
}

function KeepTopN({ disabled, onApply }: { disabled?: boolean; onApply: (n: number) => void }) {
  const [open, setOpen] = useState(false);
  const [n, setN] = useState(100);
  if (!open) return <button disabled={disabled} style={btn} onClick={() => setOpen(true)} title="Keep the first N rows (Table.FirstN)">Keep top N…</button>;
  return (
    <span className="inline-flex items-center gap-1">
      <input type="number" min={1} autoFocus value={n} onChange={(e) => setN(Math.max(1, parseInt(e.target.value || '1', 10)))}
        onKeyDown={(e) => { if (e.key === 'Enter') { onApply(n); setOpen(false); } else if (e.key === 'Escape') setOpen(false); }} style={{ ...input, width: 64 }} />
      <button style={primaryBtn} onClick={() => { onApply(n); setOpen(false); }}>Keep</button>
      <button style={btn} onClick={() => setOpen(false)}>✕</button>
    </span>
  );
}

// DAX for the column profile: one row with COUNTROWS + DISTINCTCOUNT/COUNTBLANK per column (approximate,
// robust — DISTINCTCOUNT counts a blank as one value; the caveat says "profiled from loaded data").
function profileDax(table: string, cols: string[]): string {
  const t = `'${table.replace(/'/g, "''")}'`;
  const parts = [`"rows", COUNTROWS(${t})`];
  cols.forEach((c, i) => {
    const ref = `${t}[${c.replace(/\]/g, ']]')}]`;
    parts.push(`"d${i}", DISTINCTCOUNT(${ref})`);
    parts.push(`"b${i}", COUNTBLANK(${ref})`);
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
export function AppliedStepsPanel({ text, onChange, onSelect, onError }: {
  text: string;
  onChange: (m: string) => void;
  onSelect: (from: number, to: number) => void;
  onError: (msg: string) => void;
}) {
  const shape = useMemo(() => letShape(text), [text]);
  const [editing, setEditing] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);

  const doRename = async (oldName: string) => {
    const to = draft.trim(); setEditing(null);
    if (!to || to === oldName) return;
    setBusy(true);
    try { const r = await renameStep(text, oldName, to); if (r.ok) onChange(r.m); else onError(r.error); }
    finally { setBusy(false); }
  };
  const doDelete = async (name: string) => {
    setBusy(true);
    try { const r = await deleteStep(text, name); if (r.ok) onChange(r.m); else onError(r.error); }
    finally { setBusy(false); }
  };

  return (
    <div className="rounded-lg border" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="px-3 py-2 text-[12px] font-semibold" style={{ borderBottom: '1px solid var(--sem-border)' }}>Applied steps</div>
      <div className="px-2 py-2" style={{ maxHeight: 380, overflow: 'auto' }}>
        {!shape ? (
          <div style={{ ...hint, padding: '2px 6px' }}>No let…in… steps detected. Edit the M directly.</div>
        ) : (
          <>
            {shape.steps.length === 0 && <div style={{ ...hint, padding: '2px 6px' }}>No steps detected.</div>}
            {shape.steps.map((st, i) => (
              <div key={i} className="group flex items-center gap-1 px-2 py-1 rounded-md hover:bg-[color-mix(in_srgb,var(--sem-fg)_6%,transparent)]" style={{ fontSize: 11 }}>
                <span style={{ color: 'var(--sem-muted)', width: 16 }}>{i + 1}</span>
                {editing === st.name ? (
                  <input autoFocus value={draft} disabled={busy} onChange={(e) => setDraft(e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Enter') void doRename(st.name); else if (e.key === 'Escape') setEditing(null); }}
                    onBlur={() => void doRename(st.name)} style={{ ...input, maxWidth: 'unset', flex: 1, minWidth: 0 }} />
                ) : (
                  <button onClick={() => onSelect(st.start, st.end)} title="Select this step in the editor" className="flex-1 min-w-0 truncate text-left"
                    style={{ background: 'none', border: 'none', color: 'var(--sem-fg)', cursor: 'pointer', padding: 0 }}>{st.name}</button>
                )}
                {editing !== st.name && (
                  <span className="shrink-0 flex items-center gap-0.5 opacity-0 group-hover:opacity-100 transition-opacity">
                    <button onClick={() => { setEditing(st.name); setDraft(st.name); }} disabled={busy} title="Rename step"
                      style={{ background: 'none', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer', padding: '0 2px' }}>✎</button>
                    <button onClick={() => void doDelete(st.name)} disabled={busy} title="Delete step (references re-point to the previous step)"
                      style={{ background: 'none', border: 'none', color: 'var(--sem-bad)', cursor: 'pointer', padding: '0 2px' }}>✕</button>
                  </span>
                )}
              </div>
            ))}
            <div className="px-2 py-1 truncate" style={{ fontSize: 11 }} title={shape.result}>
              <span style={{ color: 'var(--sem-muted)', marginRight: 6 }}>→</span>
              <span style={{ color: 'var(--sem-accent)' }}>(result)</span>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
