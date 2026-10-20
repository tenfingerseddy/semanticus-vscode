import { friendlyHeader, columnFormatter } from './resultformat.mjs';
import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { formatNumber, dateText } from './gridformat';
import { ColumnFilter, FilterFamily, FilterPopover, FunnelIcon, compile, describe, distinctValues, familiesOf, isActive } from './gridfilter';

export interface GridColumn {
  name: string; type?: string;
  /** The model's own format string for a measure column (0.0068 as 0.7%, 36111492.86 as $36,111,492.86); sorting and filtering still see the number. */
  format?: string;
}

const COL_W = 168;
const MIN_FIT_COL_W = 118;   // narrower than this and squeezing to fit hurts more than scrolling does
const SPREAD_COL_W = 360;   // the widest a column grows to when a narrow result spreads across its box
const ROW_H = 26;
const POP_W = 280;

function isNum(v: unknown): boolean { return typeof v === 'number'; }
function cell(v: unknown, format?: string): string {
  if (v === null || v === undefined) return '';
  const pct = typeof v === 'number' ? formatNumber(v, format) : null;
  // A date arrives as text ("2025-01-01T00:00:00"): it shows in the column's format, or as a plain short date.
  return pct ?? dateText(v, format) ?? String(v);
}

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
    <span className="shrink-0 grid place-items-center sem-num text-(length:--sem-text-label)" title={g.label}
      style={{ width: 14, height: 14, color: 'var(--sem-muted)' }}>{g.ch}</span>
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
export function ResultGrid({ columns, rows, height = 460, filterable = true, showTypeGlyph = false, onColumnMenu, menuCol, subHeader, onCellMenu, onCellClick, truncated = false, framed = true, friendly = false, countElsewhere = false }: {
  columns: GridColumn[]; rows: unknown[][]; height?: number | string; filterable?: boolean; truncated?: boolean;
  // A grid on the page is its own card: a hairline box whose first column starts one card padding in. Inside a
  // card that already frames it (DAX Lab's result, the Power Query preview) it draws no box of its own, because
  // a card never sits inside a card (One Visual Language, 23 September).
  framed?: boolean;
  // Queries (27 Sep): friendly headers, formatted dates, and columns that spread across the width the result is
  // given, so a short result is not a strip on the left. Display only; sorting, filtering and onCellMenu still
  // read the raw values.
  friendly?: boolean;
  // The caller already says how many rows there are (DAX Lab's result line), so the filter bar names a count only
  // when the filter has cut the rows down.
  countElsewhere?: boolean;
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
  // Pressing a number explains it (the Query page's Explain this number card). Same row-values contract as onCellMenu.
  onCellClick?: (row: unknown[], colIndex: number) => void;
}) {
  const parentRef = useRef<HTMLDivElement>(null);
  const [sort, setSort] = useState<Sort | null>(null);
  const [query, setQuery] = useState('');
  const [filters, setFilters] = useState<Record<number, ColumnFilter>>({});
  // The open filter box remembers its column's name and kind, so it can stay open (with what was typed in it) when the columns change under it. col -1 = not in this result.
  const [open, setOpen] = useState<{ col: number; x: number; y: number; name: string; family: FilterFamily } | null>(null);
  // Filters set aside when the result's columns changed: kept by column name, named on the page, and used again only when the person says so.
  const [aside, setAside] = useState<{ name: string; filter: ColumnFilter }[]>([]);

  const fams = useMemo(() => familiesOf(columns, rows), [columns, rows]);
  // Filters are bound by column INDEX, so a schema change (a new query, maybe the assistant's) would mis-apply them. A change of the column set never drops what the
  // person set or typed (sweep 3, site 6): applied filters are SET ASIDE by column name and the page says so, and an open filter box stays open on its column (found
  // again by name) or, when that column is gone, on its own, so the value typed in it is kept. A same-shape refresh keeps everything as it was.
  const colSig = useMemo(() => JSON.stringify(columns.map((c) => [c.name, c.type || ''])), [columns]);
  const shownCols = useRef<GridColumn[] | null>(null);
  useEffect(() => {
    const before = shownCols.current;
    shownCols.current = columns;
    if (!before) return;
    const kept = Object.keys(filters).map(Number).filter((ci) => isActive(filters[ci]) && before[ci]).map((ci) => ({ name: before[ci].name, filter: filters[ci] }));
    if (kept.length) setAside((cur) => [...cur.filter((a) => !kept.some((k) => k.name === a.name)), ...kept]);
    setFilters({});
    setOpen((o) => {
      if (!o) return o;
      const at = columns.findIndex((c, i) => c.name === o.name && fams[i] === o.family);
      return { ...o, col: at };
    });
  }, [colSig]);   // eslint-disable-line react-hooks/exhaustive-deps
  const asideFor = (name: string) => aside.find((a) => a.name === name)?.filter;
  const putAside = (name: string, f: ColumnFilter | null) => setAside((cur) => [...cur.filter((a) => a.name !== name), ...(f ? [{ name, filter: f }] : [])]);
  // Use them again: each filter goes back on the column of its name when that column is here with the same kind; the rest stay set aside.
  const useAsideAgain = () => {
    const back: Record<number, ColumnFilter> = {};
    const rest = aside.filter((a) => {
      const at = columns.findIndex((c, i) => c.name === a.name && fams[i] === a.filter.kind);
      if (at < 0) return true;
      back[at] = a.filter; return false;
    });
    setFilters((p) => ({ ...p, ...back }));
    setAside(rest);
  };
  const formatters = useMemo(() => (friendly ? columns.map((c, ci) => columnFormatter(rows, ci, c.name)) : []), [friendly, columns, rows]);

  // Distinct value list for the open text column's checklist (loaded rows only; capped inside).
  const distinct = useMemo(() => (open && open.col >= 0 && fams[open.col] === 'text' ? distinctValues(rows, open.col) : null), [open, fams, rows]);

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
  // Columns that ALMOST fit used to overflow by a sliver, so the last one was cut mid-value and the only
  // hint was a scrollbar the host may render as an overlay. Shrink them to fit instead, as long as they stay
  // readable; a genuinely wide result still scrolls (and .sem-grid-scroll makes that scrollbar visible).
  const [boxW, setBoxW] = useState(0);
  useEffect(() => {
    const el = parentRef.current; if (!el) return;
    const read = () => setBoxW(el.clientWidth);
    read();
    const ro = new ResizeObserver(read);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const colW = useMemo(() => {
    if (!boxW || columns.length === 0) return COL_W;
    // A narrow result spreads across the box instead of sitting in a strip on its left (the space rule, 27
    // September 2026), up to SPREAD_COL_W a column so two columns do not end a screen apart. The friendly grids
    // (the Queries redesign) fill the box, as their picked mockup draws them.
    if (columns.length * COL_W <= boxW)
      return friendly ? Math.floor(boxW / columns.length) : Math.min(SPREAD_COL_W, Math.floor(boxW / columns.length));
    const perCol = Math.floor(boxW / columns.length);
    return perCol >= MIN_FIT_COL_W ? perCol : COL_W;
  }, [boxW, columns.length, friendly]);
  const totalW = Math.max(columns.length * colW, 1);

  // Cycle a header: unsorted → asc → desc → unsorted.
  const onHeader = (i: number) =>
    setSort((s) => (!s || s.col !== i ? { col: i, dir: 1 } : s.dir === 1 ? { col: i, dir: -1 } : null));

  const openAt = (e: React.MouseEvent, ci: number, toggle = false) => {
    e.stopPropagation();
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    setOpen((o) => (toggle && o?.col === ci ? null : { col: ci, x: rect.left, y: rect.bottom + 4, name: columns[ci]?.name ?? '', family: fams[ci] }));
  };
  const setCol = (ci: number, f: ColumnFilter) => setFilters((p) => ({ ...p, [ci]: f }));
  const clearCol = (ci: number) => setFilters((p) => { const n = { ...p }; delete n[ci]; return n; });
  const resetAll = () => { setQuery(''); setSort(null); setFilters({}); setOpen(null); setAside([]); };

  const dirty = !!query || !!sort || activeCols.length > 0;

  return (
    <div className="flex flex-col min-h-0" style={{ height }}>
      {filterable && (
        <>
          <div className="flex items-center gap-2 pb-2 shrink-0">
            <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Filter rows…" spellCheck={false} aria-label="Filter rows"
              className="sem-field sem-field-sm w-56" />
            {(!countElsewhere || view.length !== rows.length) && <span className="sem-text-meta sem-num">
              {view.length === rows.length ? `${rows.length.toLocaleString()} rows` : `${view.length.toLocaleString()} of ${rows.length.toLocaleString()}${truncated ? ' loaded' : ''}`}
              {truncated ? '. More rows exist, and the filter searches only these.' : ''}
            </span>}
            {dirty && (
              <button onClick={resetAll} className="sem-btn sem-btn-sm sem-btn-quiet">Reset</button>
            )}
          </div>
          {activeCols.length > 0 && (
            <div className="flex items-center flex-wrap gap-2 pb-2 shrink-0">
              {activeCols.map((ci, idx) => (
                <Fragment key={ci}>
                  {idx > 0 && <span className="sem-text-meta">and</span>}
                  <span className="sem-chip sem-chip-outline">
                    <button onClick={(e) => openAt(e, ci)} className="inline-flex items-center gap-1" style={{ background: 'none', border: 'none', color: 'inherit', cursor: 'pointer', padding: 0 }} title="Edit filter">
                      <b className="font-semibold">{columns[ci]?.name}</b>
                      <span style={{ color: 'var(--sem-muted)' }}>{describe(filters[ci])}</span>
                    </button>
                    <button onClick={() => clearCol(ci)} title="Remove" className="grid place-items-center rounded-full text-(length:--sem-text-label) leading-none" style={{ width: 'var(--sem-chip-h)', height: 'var(--sem-chip-h)', background: 'transparent', color: 'var(--sem-muted)', border: 'none', cursor: 'pointer' }}>✕</button>
                  </span>
                </Fragment>
              ))}
              <button onClick={() => setFilters({})} className="sem-btn sem-btn-sm sem-btn-quiet">Clear all</button>
            </div>
          )}
          {aside.length > 0 && (
            <div role="status" data-testid="grid-filters-aside" className="flex items-center flex-wrap gap-2 pb-2 shrink-0 sem-text-meta">
              <span>The columns changed, so {aside.length === 1 ? 'this filter is' : 'these filters are'} kept but not used:</span>
              {aside.map((a) => <span key={a.name} className="sem-chip sem-chip-outline"><b className="font-semibold">{a.name}</b> <span style={{ color: 'var(--sem-muted)' }}>{describe(a.filter)}</span></span>)}
              <button onClick={useAsideAgain} className="sem-btn sem-btn-sm sem-btn-quiet">Use them again</button>
              <button onClick={() => setAside([])} className="sem-btn sem-btn-sm sem-btn-quiet">Drop them</button>
            </div>
          )}
        </>
      )}

      <div ref={parentRef} className={'sem-grid-scroll overflow-auto flex-1 min-h-0' + (framed ? ' xe-grid-framed' : '')}>
        <div style={{ minWidth: totalW, position: 'relative' }}>
          {/* sticky header — click to sort, funnel to filter, ⌄/right-click for column ops (opt-in), + an optional
              second (profiling) row that scroll- and width-syncs because it lives in the same sticky wrapper */}
          <div className="sticky top-0 z-10" style={{ background: 'var(--sem-ground)' }}>
            <div className="flex" style={{ borderBottom: subHeader ? undefined : '1px solid var(--sem-rule)' }}>
              {columns.map((c, i) => {
                const active = sort?.col === i;
                const filtered = activeCols.includes(i);
                const menuHere = menuCol === i;
                return (
                  <div key={i} onClick={() => onHeader(i)}
                    onContextMenu={onColumnMenu ? (e) => { e.preventDefault(); e.stopPropagation(); onColumnMenu(i, e.currentTarget as HTMLElement); } : undefined}
                    title={(c.type ? `${c.name} · ${c.type}` : c.name) + ' · click to sort' + (onColumnMenu ? ' · ⌄ or right-click for column operations' : '')}
                    className="xe-gcell group px-2 py-1 sem-text-meta cursor-pointer select-none flex items-center gap-1"
                    style={{ width: colW, color: active || menuHere ? 'var(--sem-fg)' : 'var(--sem-muted)', fontWeight: active ? 'var(--sem-weight-strong)' : undefined, boxShadow: menuHere ? 'inset 0 0 0 1px var(--sem-border)' : undefined }}>
                    {showTypeGlyph && <TypeGlyph type={c.type} />}
                    {/* A spread column's number sits at its right edge, so its header does too: a left header over a
                        right number read as the next column's (verify-queries-redesign D10). */}
                    <span className={'truncate flex-1 min-w-0' + (friendly && fams[i] === 'number' ? ' text-right' : '')}>{friendly ? friendlyHeader(c.name) : c.name}</span>
                    {active && <span className="shrink-0 text-(length:--sem-text-label)">{sort!.dir === 1 ? '▲' : '▼'}</span>}
                    {onColumnMenu && (
                      <button onClick={(e) => { e.stopPropagation(); onColumnMenu(i, e.currentTarget as HTMLElement); }} title="Column operations" data-colmenu={i}
                        className="shrink-0 grid place-items-center"
                        style={{ width: 'var(--sem-space-4)', height: 'var(--sem-chip-h)', padding: 0, background: 'none', border: 'none', cursor: 'pointer', color: menuHere ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>⌄</button>
                    )}
                    {filterable && (
                      <button onClick={(e) => openAt(e, i, true)} title="Filter this column" data-funnel={i}
                        className={'shrink-0 grid place-items-center transition-opacity ' + (filtered ? 'opacity-100' : 'opacity-0 group-hover:opacity-70')}
                        style={{ width: 'var(--sem-space-4)', height: 'var(--sem-chip-h)', padding: 0, background: 'none', border: 'none', cursor: 'pointer', color: filtered ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>
                        <FunnelIcon on={filtered} />
                      </button>
                    )}
                  </div>
                );
              })}
            </div>
            {subHeader && (
              <div className="flex" style={{ borderBottom: '1px solid var(--sem-rule)' }}>
                {columns.map((_, i) => (
                  <div key={i} className="xe-gcell px-2 py-0 overflow-hidden" style={{ width: colW }}>{subHeader(i)}</div>
                ))}
              </div>
            )}
          </div>
          {/* virtualized body */}
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
            {virtualizer.getVirtualItems().map((vr) => {
              const row = view[vr.index];
              return (
                <div key={vr.key} className="flex absolute left-0" style={{ top: vr.start, height: ROW_H, width: totalW }}>
                  {columns.map((_, ci) => {
                    const v = row?.[ci];
                    return (
                      <div key={ci} className={'xe-gcell px-2 py-1 text-xs truncate ' + (isNum(v) ? 'sem-num text-right' : '')}
                        onContextMenu={onCellMenu && row ? (e) => {
                          // Suppress the default menu ONLY when the caller actually handled the cell — a
                          // dimension/label cell has no explanation, so it keeps the normal context menu.
                          if (onCellMenu(row, ci, { x: e.clientX, y: e.clientY })) { e.preventDefault(); e.stopPropagation(); }
                        } : undefined}
                        onClick={onCellClick && row && isNum(v) ? () => onCellClick(row, ci) : undefined}
                        style={{ width: colW, borderBottom: '1px solid var(--sem-rule)', color: 'var(--sem-fg)', cursor: onCellClick && isNum(v) ? 'pointer' : undefined }}
                        title={cell(v, columns[ci]?.format) + ((onCellMenu || onCellClick) && isNum(v) ? '\nPress to explain this number' : '')}>
                        {friendly && !(isNum(v) && formatNumber(v as number, columns[ci]?.format)) ? (columns[ci]?.format && dateText(v, columns[ci]?.format)) || formatters[ci](v) : cell(v, columns[ci]?.format)}
                      </div>
                    );
                  })}
                </div>
              );
            })}
          </div>
        </div>
      </div>

      {open && (open.col >= 0 ? fams[open.col] : open.family) && (
        <>
          <div className="fixed inset-0" style={{ zIndex: 40 }} onClick={() => setOpen(null)} />
          <div className="fixed" style={{ zIndex: 50, top: open.y, left: Math.max(8, Math.min(open.x, (typeof window !== 'undefined' ? window.innerWidth : 1200) - POP_W - 8)) }} onClick={(e) => e.stopPropagation()}>
            {open.col < 0 && <div role="status" data-testid="grid-filter-col-gone" className="sem-text-meta mb-1">{open.name} is not in this result now. What you set here is kept, and Apply sets it aside.</div>}
            <FilterPopover
              family={open.col >= 0 ? fams[open.col] : open.family} name={open.name} initial={open.col >= 0 ? filters[open.col] : asideFor(open.name)} distinct={distinct}
              onApply={(f) => { if (open.col >= 0) setCol(open.col, f); else putAside(open.name, f); setOpen(null); }}
              onClear={() => { if (open.col >= 0) clearCol(open.col); else putAside(open.name, null); setOpen(null); }}
            />
          </div>
        </>
      )}
    </div>
  );
}
