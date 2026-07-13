import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { ColumnFilter, FilterPopover, FunnelIcon, compile, describe, distinctValues, familiesOf, isActive } from './gridfilter';

export interface GridColumn { name: string; type?: string }

const COL_W = 168;
const ROW_H = 26;
const POP_W = 280;

function isNum(v: unknown): boolean { return typeof v === 'number'; }
function cell(v: unknown): string { return v === null || v === undefined ? '' : String(v); }

// A compact, always-rendering type glyph for a column header (opt-in via `showTypeGlyph`). Single safe
// Unicode chars so headless Chromium renders them without font fallback tofu. Order matters: date/time is
// tested before number so "DateTime" isn't mis-tagged.
const TYPE_GLYPHS: { test: RegExp; ch: string; label: string }[] = [
  { test: /date|time/i, ch: '◷', label: 'date / time' },
  { test: /bool|logical/i, ch: '✓', label: 'true / false' },
  { test: /int|dec|doub|number|curr|float|real|money|percent/i, ch: '#', label: 'number' },
  { test: /binary/i, ch: '▦', label: 'binary' },
  { test: /str|text|char/i, ch: 'A', label: 'text' },
];
function typeGlyph(type?: string) { return type ? TYPE_GLYPHS.find((g) => g.test.test(type)) ?? null : null; }
function TypeGlyph({ type }: { type?: string }) {
  const g = typeGlyph(type);
  if (!g) return null;
  return (
    <span className="shrink-0 grid place-items-center rounded text-[9px] font-semibold tnum" title={g.label}
      style={{ width: 14, height: 14, background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{g.ch}</span>
  );
}

// Type-aware comparator: nulls sort last; numbers numerically; everything else as a natural (numeric-aware)
// string compare, so "Item 2" < "Item 10" and mixed numeric strings order intuitively.
function compareVals(a: unknown, b: unknown): number {
  const an = a === null || a === undefined, bn = b === null || b === undefined;
  if (an && bn) return 0; if (an) return 1; if (bn) return -1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
}

interface Sort { col: number; dir: 1 | -1 }

/**
 * Virtualized result grid — renders only the visible rows, so it stays smooth on 100k-row result sets
 * where a plain <table> would lock up. Sticky header, horizontal scroll for wide tables.
 *
 * Interaction: click a column header to sort (asc → desc → off, type-aware). The fast filter box keeps
 * rows where any cell contains the query. ADVANCED: each header carries a funnel → a per-column,
 * type-shaped filter (date range / relative, numeric range/ops, text contains/value-pick, boolean). Active
 * column filters show as removable chips and AND together (and with the fast box). All filtering/sorting is
 * view-only — `rows` are never mutated, and nothing re-queries the engine. `filterable={false}` hides the
 * whole filter bar (and the funnels) — used for tiny embedded grids.
 */
export function ResultGrid({ columns, rows, height = 460, filterable = true, showTypeGlyph = false, onColumnMenu, menuCol, subHeader, onCellMenu }: {
  columns: GridColumn[]; rows: unknown[][]; height?: number | string; filterable?: boolean;
  // Opt-in header affordances (used by the M Code sample so its preview headers double as the column-op surface):
  showTypeGlyph?: boolean;                                            // a small type badge before each column name
  onColumnMenu?: (colIndex: number, anchor: HTMLElement) => void;     // ⌄ / right-click → open the caller's column-ops menu
  menuCol?: number | null;                                           // the column whose menu is currently open (accent the header)
  subHeader?: (colIndex: number) => React.ReactNode;                 // a second sticky header row (e.g. the profiling strip), column-aligned
  // Opt-in BODY-cell context menu (mirrors onColumnMenu; used by DAX Lab's "Explain this number"). Passes the
  // clicked VIEW row's values (not an index — the view is sorted/filtered client-side, so indexes would lie).
  // Return TRUE when the cell was handled: only then is the browser's default context menu suppressed —
  // label/dimension cells with no explanation keep their normal menu (PR #92 review, Finding C).
  onCellMenu?: (row: unknown[], colIndex: number, anchor: { x: number; y: number }) => boolean;
}) {
  const parentRef = useRef<HTMLDivElement>(null);
  const [sort, setSort] = useState<Sort | null>(null);
  const [query, setQuery] = useState('');
  const [filters, setFilters] = useState<Record<number, ColumnFilter>>({});
  const [open, setOpen] = useState<{ col: number; x: number; y: number } | null>(null);

  // Filters are bound by column INDEX, so a schema change (new query) would mis-apply them — reset on the
  // column signature, not on every render (a same-shape refresh keeps the user's filters).
  const colSig = useMemo(() => columns.map((c) => c.name + '' + (c.type || '')).join(''), [columns]);
  useEffect(() => { setFilters({}); setOpen(null); }, [colSig]);

  const fams = useMemo(() => familiesOf(columns, rows), [columns, rows]);

  // Distinct value list for the open text column's checklist (loaded rows only; capped inside).
  const distinct = useMemo(() => (open && fams[open.col] === 'text' ? distinctValues(rows, open.col) : null), [open, fams, rows]);

  const activeCols = useMemo(
    () => Object.keys(filters).map(Number).filter((ci) => isActive(filters[ci])),
    [filters],
  );

  // Filter (fast box, then per-column predicates), then sort — all derived (memoized), source rows untouched.
  const view = useMemo(() => {
    const now = Date.now(); // relative-date windows re-evaluate against "now" each filter pass — deterministic & current
    const preds = activeCols.map((ci) => [ci, compile(filters[ci], now)] as const);
    let r = rows;
    const q = query.trim().toLowerCase();
    if (q) r = r.filter((row) => row?.some((v) => v !== null && v !== undefined && String(v).toLowerCase().includes(q)));
    if (preds.length) r = r.filter((row) => preds.every(([ci, pred]) => pred(row?.[ci])));
    if (sort) { const { col, dir } = sort; r = [...r].sort((a, b) => compareVals(a?.[col], b?.[col]) * dir); }
    return r;
  }, [rows, query, sort, filters, activeCols]);

  const virtualizer = useVirtualizer({
    count: view.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => ROW_H,
    overscan: 14,
  });
  const totalW = Math.max(columns.length * COL_W, 1);

  // Cycle a header: unsorted → asc → desc → unsorted.
  const onHeader = (i: number) =>
    setSort((s) => (!s || s.col !== i ? { col: i, dir: 1 } : s.dir === 1 ? { col: i, dir: -1 } : null));

  const openAt = (e: React.MouseEvent, ci: number, toggle = false) => {
    e.stopPropagation();
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    setOpen((o) => (toggle && o?.col === ci ? null : { col: ci, x: rect.left, y: rect.bottom + 4 }));
  };
  const setCol = (ci: number, f: ColumnFilter) => setFilters((p) => ({ ...p, [ci]: f }));
  const clearCol = (ci: number) => setFilters((p) => { const n = { ...p }; delete n[ci]; return n; });
  const resetAll = () => { setQuery(''); setSort(null); setFilters({}); setOpen(null); };

  const dirty = !!query || !!sort || activeCols.length > 0;

  return (
    <div className="flex flex-col min-h-0" style={{ height }}>
      {filterable && (
        <>
          <div className="flex items-center gap-2 pb-1.5 shrink-0">
            <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Filter rows…" spellCheck={false}
              className="text-[12px] px-2 py-1 rounded-md outline-none w-56" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
            <span className="text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>
              {view.length === rows.length ? `${rows.length.toLocaleString()} rows` : `${view.length.toLocaleString()} of ${rows.length.toLocaleString()}`}
            </span>
            {dirty && (
              <button onClick={resetAll} className="text-[11px] px-1.5 py-0.5 rounded-md" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>Reset</button>
            )}
          </div>
          {activeCols.length > 0 && (
            <div className="flex items-center flex-wrap gap-1.5 pb-1.5 shrink-0">
              {activeCols.map((ci, idx) => (
                <Fragment key={ci}>
                  {idx > 0 && <span className="text-[9px] font-bold tracking-wider" style={{ color: 'var(--sem-muted)' }}>AND</span>}
                  <span className="inline-flex items-center gap-1.5 rounded-full text-[11.5px] pl-2.5 pr-1 py-0.5"
                    style={{ background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb,var(--sem-accent) 55%, transparent)', color: 'var(--sem-fg)' }}>
                    <button onClick={(e) => openAt(e, ci)} className="inline-flex items-center gap-1" style={{ background: 'none', border: 'none', color: 'inherit', cursor: 'pointer', padding: 0 }} title="Edit filter">
                      <b className="font-semibold">{columns[ci]?.name}</b>
                      <span style={{ color: 'color-mix(in srgb,var(--sem-fg) 72%, var(--sem-accent))' }}>{describe(filters[ci])}</span>
                    </button>
                    <button onClick={() => clearCol(ci)} title="Remove" className="grid place-items-center rounded-full text-[10px] leading-none" style={{ width: 15, height: 15, background: 'rgba(255,255,255,.08)', color: 'var(--sem-muted)', border: 'none', cursor: 'pointer' }}>✕</button>
                  </span>
                </Fragment>
              ))}
              <button onClick={() => setFilters({})} className="text-[11px] ml-1" style={{ color: 'var(--sem-muted)', background: 'none', border: 'none', cursor: 'pointer', textDecoration: 'underline', textUnderlineOffset: 2 }}>Clear all</button>
            </div>
          )}
        </>
      )}

      <div ref={parentRef} className="overflow-auto rounded-lg flex-1 min-h-0" style={{ border: '1px solid var(--sem-border)' }}>
        <div style={{ minWidth: totalW, position: 'relative' }}>
          {/* sticky header — click to sort, funnel to filter, ⌄/right-click for column ops (opt-in), + an optional
              second (profiling) row that scroll- and width-syncs because it lives in the same sticky wrapper */}
          <div className="sticky top-0 z-10" style={{ background: 'var(--sem-surface-2)' }}>
            <div className="flex" style={{ borderBottom: subHeader ? undefined : '1px solid var(--sem-border)' }}>
              {columns.map((c, i) => {
                const active = sort?.col === i;
                const filtered = activeCols.includes(i);
                const menuHere = menuCol === i;
                return (
                  <div key={i} onClick={() => onHeader(i)}
                    onContextMenu={onColumnMenu ? (e) => { e.preventDefault(); e.stopPropagation(); onColumnMenu(i, e.currentTarget as HTMLElement); } : undefined}
                    title={(c.type ? `${c.name} · ${c.type}` : c.name) + ' · click to sort' + (onColumnMenu ? ' · ⌄ or right-click for column operations' : '')}
                    className="group px-2 py-1 text-[11px] font-semibold cursor-pointer select-none flex items-center gap-1"
                    style={{ width: COL_W, color: active ? 'var(--sem-accent)' : 'var(--sem-fg)', boxShadow: menuHere ? 'inset 0 0 0 1px var(--sem-accent)' : undefined }}>
                    {showTypeGlyph && <TypeGlyph type={c.type} />}
                    <span className="truncate flex-1 min-w-0">{c.name}</span>
                    {active && <span className="shrink-0 text-[9px]">{sort!.dir === 1 ? '▲' : '▼'}</span>}
                    {onColumnMenu && (
                      <button onClick={(e) => { e.stopPropagation(); onColumnMenu(i, e.currentTarget as HTMLElement); }} title="Column operations" data-colmenu={i}
                        className={'shrink-0 grid place-items-center transition-opacity ' + (menuHere ? 'opacity-100' : 'opacity-60 group-hover:opacity-100')}
                        style={{ width: 16, height: 16, padding: 0, background: 'none', border: 'none', cursor: 'pointer', color: menuHere ? 'var(--sem-accent)' : 'var(--sem-muted)' }}>⌄</button>
                    )}
                    {filterable && (
                      <button onClick={(e) => openAt(e, i, true)} title="Filter this column" data-funnel={i}
                        className={'shrink-0 grid place-items-center transition-opacity ' + (filtered ? 'opacity-100' : 'opacity-0 group-hover:opacity-70')}
                        style={{ width: 16, height: 16, padding: 0, background: 'none', border: 'none', cursor: 'pointer', color: filtered ? 'var(--sem-accent)' : 'var(--sem-muted)' }}>
                        <FunnelIcon on={filtered} />
                      </button>
                    )}
                  </div>
                );
              })}
            </div>
            {subHeader && (
              <div className="flex" style={{ borderBottom: '1px solid var(--sem-border)' }}>
                {columns.map((_, i) => (
                  <div key={i} className="px-2 py-0.5 overflow-hidden" style={{ width: COL_W }}>{subHeader(i)}</div>
                ))}
              </div>
            )}
          </div>
          {/* virtualized body */}
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
            {virtualizer.getVirtualItems().map((vr) => {
              const row = view[vr.index];
              return (
                <div key={vr.key} className="flex absolute left-0" style={{ top: vr.start, height: ROW_H, width: totalW, background: vr.index % 2 ? 'transparent' : 'color-mix(in srgb,var(--sem-fg) 3%, transparent)' }}>
                  {columns.map((_, ci) => {
                    const v = row?.[ci];
                    return (
                      <div key={ci} className={'px-2 py-1 text-[12px] truncate ' + (isNum(v) ? 'tnum text-right' : '')}
                        onContextMenu={onCellMenu && row ? (e) => {
                          // Suppress the default menu ONLY when the caller actually handled the cell — a
                          // dimension/label cell has no explanation, so it keeps the normal context menu.
                          if (onCellMenu(row, ci, { x: e.clientX, y: e.clientY })) { e.preventDefault(); e.stopPropagation(); }
                        } : undefined}
                        style={{ width: COL_W, borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}
                        title={cell(v) + (onCellMenu && isNum(v) ? '\nRight-click: explain this number' : '')}>
                        {cell(v)}
                      </div>
                    );
                  })}
                </div>
              );
            })}
          </div>
        </div>
      </div>

      {open && fams[open.col] && (
        <>
          <div className="fixed inset-0" style={{ zIndex: 40 }} onClick={() => setOpen(null)} />
          <div className="fixed" style={{ zIndex: 50, top: open.y, left: Math.max(8, Math.min(open.x, (typeof window !== 'undefined' ? window.innerWidth : 1200) - POP_W - 8)) }} onClick={(e) => e.stopPropagation()}>
            <FilterPopover
              family={fams[open.col]} name={columns[open.col]?.name ?? ''} initial={filters[open.col]} distinct={distinct}
              onApply={(f) => { setCol(open.col, f); setOpen(null); }}
              onClear={() => { clearCol(open.col); setOpen(null); }}
            />
          </div>
        </>
      )}
    </div>
  );
}
