// Power-BI-style field wells for DAX Lab's Visual mode. Drag fields from the Fields rail into Rows / Columns
// / Values / Filters; the wells compose a SUMMARIZECOLUMNS (+ CALCULATETABLE) query that runs through the SAME
// runDax path the editor uses — so a visual IS a query you can benchmark. Filters reuse the grid's filter
// popovers (date range/relative, numeric, text) and translate to DAX boolean predicates, so "filter context"
// is something you build by dragging, not hand-typing.

import { useState } from 'react';
import type { DaxModel } from './dax';
import { ColumnFilter, FilterFamily, FilterPopover, describe } from './gridfilter';
import { uiLabel } from './copy';

export type FieldKind = 'measure' | 'column' | 'table';
export interface WellField { kind: FieldKind; ref: string; name: string; table?: string }
export interface PivotFilter { ref: string; name: string; family: FilterFamily; filter: ColumnFilter }
export interface PivotConfig { rows: WellField[]; cols: WellField[]; values: WellField[]; filters: PivotFilter[] }

export const EMPTY_CONFIG: PivotConfig = { rows: [], cols: [], values: [], filters: [] };

// Parse the text/plain DAX reference the Fields rail puts on the drag (no rail change needed).
export function parseRef(text: string): WellField | null {
  const t = text.trim();
  let m: RegExpExecArray | null;
  if ((m = /^'([^']+)'\[(.+)\]$/.exec(t))) return { kind: 'column', ref: t, table: m[1], name: m[2] };
  if ((m = /^([A-Za-z_]\w*)\[(.+)\]$/.exec(t))) return { kind: 'column', ref: `'${m[1]}'[${m[2]}]`, table: m[1], name: m[2] };
  if ((m = /^\[(.+)\]$/.exec(t))) return { kind: 'measure', ref: t, name: m[1] };
  if ((m = /^'([^']+)'$/.exec(t))) return { kind: 'table', ref: t, name: m[1], table: m[1] };
  return null;
}

// No column type metadata in DaxModel → guess the filter family from the name. Good enough to pick the right
// editor; the user can still adjust. (A later pass can fetch real types via list_columns.)
const DATE_RE = /date|year|month|quarter|day|week|fiscal|period|time/i;
const NUM_RE = /amount|price|cost|qty|quantity|count|sum|total|value|sales|revenue|margin|rate|pct|percent|ratio|duration|ms|seconds|num|size|score|age|balance|weight/i;
export function familyByName(name: string): FilterFamily {
  if (DATE_RE.test(name)) return 'date';
  if (NUM_RE.test(name)) return 'number';
  return 'text';
}

const esc = (s: string) => s.replace(/"/g, '""');

// One DAX boolean predicate for a column filter — the bridge that turns a dropped filter into filter context.
export function filterToDax(pf: PivotFilter): string | null {
  const ref = pf.ref;
  const f = pf.filter;
  switch (f.kind) {
    case 'boolean': return `${ref} = ${f.value ? 'TRUE()' : 'FALSE()'}`;
    case 'number': {
      const { op, a, b } = f;
      if (op === 'blank') return `ISBLANK(${ref})`;
      if (op === 'nblank') return `NOT ISBLANK(${ref})`;
      if (op === 'between') {
        const parts: string[] = [];
        if (a != null) parts.push(`${ref} >= ${a}`);
        if (b != null) parts.push(`${ref} <= ${b}`);
        return parts.length ? parts.join(' && ') : null;
      }
      if (a == null) return null;
      const OPS: Record<string, string> = { eq: '=', neq: '<>', gt: '>', gte: '>=', lt: '<', lte: '<=' };
      return `${ref} ${OPS[op]} ${a}`;
    }
    case 'text': {
      const v = f.value.trim();
      const conds: string[] = [];
      if (f.picked.length) conds.push(`${ref} IN {${f.picked.map((p) => `"${esc(p)}"`).join(', ')}}`);
      if (f.op === 'blank') return `ISBLANK(${ref})`;
      if (f.op === 'nblank') return `NOT ISBLANK(${ref})`;
      if (v) {
        if (f.op === 'contains') conds.push(`CONTAINSSTRING(${ref}, "${esc(v)}")`);
        else if (f.op === 'ncontains') conds.push(`NOT CONTAINSSTRING(${ref}, "${esc(v)}")`);
        else if (f.op === 'eq') conds.push(`${ref} = "${esc(v)}"`);
        else if (f.op === 'starts') conds.push(`LEFT(${ref}, ${v.length}) = "${esc(v)}"`);
        else if (f.op === 'ends') conds.push(`RIGHT(${ref}, ${v.length}) = "${esc(v)}"`);
      }
      return conds.length ? conds.join(' && ') : null;
    }
    case 'date': {
      if (f.mode === 'range') {
        const parts: string[] = [];
        if (f.from) parts.push(`${ref} >= ${daxDate(f.from)}`);
        if (f.to) parts.push(`${ref} <= ${daxDate(f.to)}`);
        return parts.length ? parts.join(' && ') : null;
      }
      if (f.mode === 'rel') {
        const n = Math.max(1, f.n | 0);
        let lo: string;
        if (f.unit === 'day') lo = `TODAY() - ${n - 1}`;
        else if (f.unit === 'week') lo = `TODAY() - ${n * 7 - 1}`;
        else if (f.unit === 'month') lo = `EDATE(TODAY(), -${n})`;
        else if (f.unit === 'quarter') lo = `EDATE(TODAY(), -${n * 3})`;
        else lo = `EDATE(TODAY(), -${n * 12})`;
        return `${ref} >= ${lo} && ${ref} <= TODAY()`;
      }
      // calendar periods
      switch (f.period) {
        case 'today': return `${ref} >= TODAY() && ${ref} < TODAY() + 1`;
        case 'thisWeek': return `${ref} >= TODAY() - WEEKDAY(TODAY(), 2) + 1 && ${ref} < TODAY() - WEEKDAY(TODAY(), 2) + 8`;
        case 'thisMonth': return `${ref} >= DATE(YEAR(TODAY()), MONTH(TODAY()), 1) && ${ref} < EDATE(DATE(YEAR(TODAY()), MONTH(TODAY()), 1), 1)`;
        case 'thisQuarter': return `${ref} >= DATE(YEAR(TODAY()), (QUARTER(TODAY()) - 1) * 3 + 1, 1) && ${ref} < EDATE(DATE(YEAR(TODAY()), (QUARTER(TODAY()) - 1) * 3 + 1, 1), 3)`;
        case 'thisYear': return `${ref} >= DATE(YEAR(TODAY()), 1, 1) && ${ref} < DATE(YEAR(TODAY()) + 1, 1, 1)`;
        case 'ytd': return `${ref} >= DATE(YEAR(TODAY()), 1, 1) && ${ref} <= TODAY()`;
      }
      return null;
    }
  }
}

function daxDate(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return m ? `DATE(${+m[1]}, ${+m[2]}, ${+m[3]})` : `TODAY()`;
}

// Build the runnable EVALUATE query from the wells.
export function buildQuery(c: PivotConfig): string {
  const group = [...c.rows, ...c.cols].map((f) => f.ref);
  const valExt = c.values.map((v) => `"${v.name}", ${v.ref}`);
  const inner = [...group, ...valExt];
  if (!inner.length) return 'EVALUATE\nROW("(drag fields into the wells)", BLANK())';
  const sc = `SUMMARIZECOLUMNS(\n    ${inner.join(',\n    ')}\n)`;
  const filters = c.filters.map(filterToDax).filter((x): x is string => !!x);
  if (!filters.length) return `EVALUATE\n${sc}`;
  const indented = sc.split('\n').map((l, i) => (i === 0 ? l : '    ' + l)).join('\n');
  return `EVALUATE\nCALCULATETABLE(\n    ${indented},\n    ${filters.join(',\n    ')}\n)`;
}

// Like buildQuery, but wraps each Values measure in EVALUATEANDLOG so the Debug tab captures a labelled
// snapshot per measure — lets "Log each measure" produce trace output without the user hand-editing DAX.
export function buildInstrumentedQuery(c: PivotConfig): string {
  if (!c.values.length) return buildQuery(c);
  const group = [...c.rows, ...c.cols].map((f) => f.ref);
  const valExt = c.values.map((v) => `"${v.name}", EVALUATEANDLOG(${v.ref}, "${v.name}")`);
  const sc = `SUMMARIZECOLUMNS(\n    ${[...group, ...valExt].join(',\n    ')}\n)`;
  const filters = c.filters.map(filterToDax).filter((x): x is string => !!x);
  if (!filters.length) return `EVALUATE\n${sc}`;
  const indented = sc.split('\n').map((l, i) => (i === 0 ? l : '    ' + l)).join('\n');
  return `EVALUATE\nCALCULATETABLE(\n    ${indented},\n    ${filters.join(',\n    ')}\n)`;
}

// Sensible, RUNNABLE defaults from the live model — never dead Contoso examples. First measure → Values,
// a date-ish column → Columns, another dimension column → Rows.
export function seedConfig(model: DaxModel): PivotConfig {
  const col = (c: { table: string; name: string }): WellField => ({ kind: 'column', ref: `'${c.table}'[${c.name}]`, name: c.name, table: c.table });
  const meas = model.measures[0];
  const dateCol = model.columns.find((c) => DATE_RE.test(c.name));
  // a low-cardinality-ish dimension: prefer a non-key text column not on the measure's table
  const dim = model.columns.find((c) => !/key$|id$/i.test(c.name) && c !== dateCol && (!meas || c.table !== meas.table))
    ?? model.columns.find((c) => !/key$|id$/i.test(c.name) && c !== dateCol)
    ?? model.columns.find((c) => c !== dateCol);
  return {
    rows: dim ? [col(dim)] : [],
    cols: dateCol ? [col(dateCol)] : [],
    values: meas ? [{ kind: 'measure', ref: `[${meas.name}]`, name: meas.name, table: meas.table }] : [],
    filters: [],
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// UI

const FI: Record<FieldKind | 'date', { glyph: string; color: string }> = {
  measure: { glyph: 'fx', color: 'var(--sem-accent)' },
  column: { glyph: '▭', color: '#9cdcfe' },
  table: { glyph: '▦', color: '#4ec9b0' },
  date: { glyph: '▭', color: 'var(--sem-warn)' },
};
function FieldGlyph({ f }: { f: WellField | { kind: FieldKind; name: string } }) {
  const isDate = f.kind === 'column' && DATE_RE.test(f.name);
  const g = isDate ? FI.date : FI[f.kind];
  return <span style={{ color: g.color, fontStyle: g.glyph === 'fx' ? 'italic' : 'normal', fontWeight: g.glyph === 'fx' ? 700 : 400, fontSize: 11, display: 'inline-flex', width: 13, justifyContent: 'center' }}>{g.glyph}</span>;
}

const WELL_META: { key: keyof Omit<PivotConfig, 'filters'> | 'filters'; label: string; accepts: FieldKind[]; role: string }[] = [
  { key: 'rows', label: 'Rows', accepts: ['column'], role: '#9cdcfe' },
  { key: 'cols', label: 'Columns', accepts: ['column'], role: '#9cdcfe' },
  { key: 'values', label: 'Values', accepts: ['measure'], role: 'var(--sem-accent)' },
  { key: 'filters', label: 'Filters', accepts: ['column'], role: 'var(--sem-accent)' },
];

export function FieldWells({ config, onConfig, onCompare }: { config: PivotConfig; onConfig: (c: PivotConfig) => void; onCompare?: (valueRef: string) => void }) {
  const [over, setOver] = useState<string | null>(null);
  const [editFilter, setEditFilter] = useState<{ idx: number; x: number; y: number } | null>(null);
  const [dropMsg, setDropMsg] = useState<string | null>(null);

  const drop = (wellKey: string, accepts: FieldKind[]) => (e: React.DragEvent) => {
    e.preventDefault(); setOver(null);
    const f = parseRef(e.dataTransfer.getData('text/plain'));
    if (!f) return;
    if (!accepts.includes(f.kind)) {
      setDropMsg(`${wellKey === 'values' ? 'Values' : wellKey === 'filters' ? 'Filters' : uiLabel(wellKey)} takes ${accepts.map((kind) => uiLabel(kind)).join(' or ')}, not ${uiLabel(f.kind).toLowerCase()}`);
      setTimeout(() => setDropMsg(null), 2200);
      return;
    }
    if (wellKey === 'filters') {
      if (config.filters.some((x) => x.ref === f.ref)) return;
      const family = familyByName(f.name);
      const init = defaultFilter(family);
      onConfig({ ...config, filters: [...config.filters, { ref: f.ref, name: f.name, family, filter: init }] });
    } else {
      const arr = config[wellKey as 'rows' | 'cols' | 'values'];
      if (arr.some((x) => x.ref === f.ref)) return;
      onConfig({ ...config, [wellKey]: [...arr, f] });
    }
  };

  const removeAt = (wellKey: keyof PivotConfig, i: number) => {
    const arr = config[wellKey] as unknown[];
    onConfig({ ...config, [wellKey]: arr.filter((_, j) => j !== i) });
  };

  return (
    <div className="relative">
      <div className="grid gap-2.5 p-3" style={{ gridTemplateColumns: 'repeat(4, 1fr)' }}>
        {WELL_META.map((w) => {
          const isOver = over === w.key;
          return (
            <div key={w.key} onDragOver={(e) => { e.preventDefault(); setOver(w.key); }} onDragLeave={() => setOver((o) => (o === w.key ? null : o))} onDrop={drop(w.key, w.accepts)}
              className="rounded-[9px] px-2.5 py-2" style={{ minHeight: 56, background: 'var(--sem-surface)', border: `1px dashed ${isOver ? 'var(--sem-accent)' : 'var(--sem-border-strong, var(--sem-border))'}`, boxShadow: isOver ? '0 0 0 2px var(--sem-accent-soft) inset' : 'none' }}>
              <div className="flex items-center gap-1.5 mb-1.5 text-[9.5px] font-bold uppercase" style={{ letterSpacing: '.09em', color: 'var(--sem-muted)' }}>
                <span style={{ width: 6, height: 6, borderRadius: 2, background: w.role }} />{w.label}
              </div>
              <div className="flex flex-wrap gap-1.5 items-center">
                {w.key === 'filters'
                  ? config.filters.map((pf, i) => (
                    <span key={pf.ref} className="inline-flex items-center gap-1.5 text-[11.5px] rounded-[7px] pl-2 pr-1.5 py-0.5" style={{ background: 'var(--sem-accent-soft)', border: '1px solid var(--sem-accent-line)' }}>
                      <FieldGlyph f={{ kind: 'column', name: pf.name }} />
                      <button onClick={(e) => { const r = (e.currentTarget as HTMLElement).getBoundingClientRect(); setEditFilter({ idx: i, x: r.left, y: r.bottom + 4 }); }} className="inline-flex items-center gap-1" style={{ background: 'none', border: 'none', color: 'inherit', cursor: 'pointer', padding: 0 }} title="Edit filter">
                        <b style={{ fontWeight: 600 }}>{pf.name}</b><span style={{ color: 'color-mix(in srgb,var(--sem-fg) 72%, var(--sem-accent))' }}>{describe(pf.filter)}</span>
                      </button>
                      <span onClick={() => removeAt('filters', i)} className="cursor-pointer" style={{ color: 'var(--sem-muted)', fontSize: 11 }}>✕</span>
                    </span>
                  ))
                  : config[w.key as 'rows' | 'cols' | 'values'].map((f, i) => (
                    <span key={f.ref} className="inline-flex items-center gap-1.5 text-[11.5px] rounded-[7px] pl-2 pr-1.5 py-0.5" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
                      <FieldGlyph f={f} /><span>{f.name}</span>
                      {w.key === 'values' && onCompare && <span onClick={() => onCompare(f.ref)} title="Verify a rewrite of this measure across the matrix" className="cursor-pointer" style={{ color: 'var(--sem-muted)', fontSize: 11 }}>⇄</span>}
                      <span onClick={() => removeAt(w.key as keyof PivotConfig, i)} className="cursor-pointer" style={{ color: 'var(--sem-muted)', fontSize: 11 }}>✕</span>
                    </span>
                  ))}
                {((w.key === 'filters' && config.filters.length === 0) || (w.key !== 'filters' && config[w.key as 'rows' | 'cols' | 'values'].length === 0)) && (
                  <span className="text-[11px]" style={{ color: 'var(--sem-muted)', opacity: .7 }}>drag {w.accepts.join('/')} here</span>
                )}
              </div>
            </div>
          );
        })}
      </div>
      {dropMsg && <div className="absolute left-3 -bottom-5 text-[11px]" style={{ color: 'var(--sem-warn)' }}>{dropMsg}</div>}

      {editFilter && config.filters[editFilter.idx] && (
        <>
          <div className="fixed inset-0" style={{ zIndex: 40 }} onClick={() => setEditFilter(null)} />
          <div className="fixed" style={{ zIndex: 50, top: editFilter.y, left: Math.max(8, Math.min(editFilter.x, (typeof window !== 'undefined' ? window.innerWidth : 1200) - 296)) }} onClick={(e) => e.stopPropagation()}>
            <FilterPopover family={config.filters[editFilter.idx].family} name={config.filters[editFilter.idx].name} initial={config.filters[editFilter.idx].filter}
              onApply={(filter) => { const next = [...config.filters]; next[editFilter.idx] = { ...next[editFilter.idx], filter }; onConfig({ ...config, filters: next }); setEditFilter(null); }}
              onClear={() => { removeAt('filters', editFilter.idx); setEditFilter(null); }} />
          </div>
        </>
      )}
    </div>
  );
}

function defaultFilter(family: FilterFamily): ColumnFilter {
  switch (family) {
    case 'number': return { kind: 'number', op: 'between', a: null, b: null };
    case 'date': return { kind: 'date', mode: 'rel', n: 6, unit: 'month' };
    case 'boolean': return { kind: 'boolean', value: true };
    default: return { kind: 'text', op: 'contains', value: '', picked: [] };
  }
}
