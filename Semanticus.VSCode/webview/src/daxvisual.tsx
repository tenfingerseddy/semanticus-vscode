// DAX Lab's visual surface: render the run result as a matrix or an ECharts visual, and — the headline —
// show the FILTER CONTEXT behind any data point on hover (the row/column keys + active filters, tagged by
// source, plus the equivalent CALCULATE). All derived client-side from the result + the wells; no extra
// engine calls.

import { useMemo, useState } from 'react';
import * as echarts from 'echarts';
import { EChart } from './echart';
import { ResultGrid } from './grid';
import type { ResultSet } from './wire';
import { PivotConfig, filterToDax } from './daxwells';
import type { ExplainPayload } from './explainpanel';

export type VizType = 'matrix' | 'table' | 'bar' | 'line' | 'area' | 'card' | 'scatter';

const fmtKey = (v: unknown) => (v === null || v === undefined ? '' : String(v));
const fmtVal = (v: unknown) => (v === null || v === undefined ? '' : typeof v === 'number' ? v.toLocaleString(undefined, { maximumFractionDigits: 4 }) : String(v));
const MAX_ROWS = 400, MAX_COLS = 60;

function cssVar(n: string, f: string) { const v = getComputedStyle(document.documentElement).getPropertyValue(n).trim(); return v || f; }
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => (c === '&' ? '&amp;' : c === '<' ? '&lt;' : c === '>' ? '&gt;' : '&quot;'));

interface Matrix {
  rowFieldNames: string[]; colFieldNames: string[]; colKeys: string[]; colIsValues: boolean;
  rows: { keys: unknown[]; cells: unknown[] }[]; totalRows: number; truncatedCols: boolean; measureName: string;
}

// Tabulate the flat SUMMARIZECOLUMNS result into a row × column matrix using the wells' shape.
export function tabulate(res: ResultSet, config: PivotConfig): Matrix | null {
  const nRow = config.rows.length, nCol = config.cols.length, nVal = config.values.length;
  if (!res.rows.length || (nRow === 0 && nVal === 0)) return null;
  const rowFieldNames = config.rows.map((f) => f.name);
  const colFieldNames = config.cols.map((f) => f.name);
  const valStart = nRow + nCol;
  const colIsValues = nCol === 0;
  const measureName = config.values[0]?.name ?? 'Value';

  if (colIsValues) {
    // columns ARE the value measures; one result row per row-group.
    const colKeys = nVal ? config.values.map((v) => v.name) : ['Value'];
    const rows = res.rows.slice(0, MAX_ROWS).map((r) => ({ keys: r.slice(0, nRow), cells: r.slice(valStart, valStart + Math.max(1, nVal)) }));
    return { rowFieldNames, colFieldNames, colKeys, colIsValues, rows, totalRows: res.rows.length, truncatedCols: false, measureName };
  }

  // pivot: distinct column keys (composite of col fields), first value measure as the cell.
  const colKeySet: string[] = []; const seenCol = new Set<string>();
  const rowMap = new Map<string, { keys: unknown[]; byCol: Map<string, unknown> }>(); const rowOrder: string[] = [];
  for (const r of res.rows) {
    const ck = r.slice(nRow, nRow + nCol).map(fmtKey).join(' / ');
    if (!seenCol.has(ck) && colKeySet.length < MAX_COLS) { seenCol.add(ck); colKeySet.push(ck); }
    const rk = r.slice(0, nRow).map(fmtKey).join('␟');
    let e = rowMap.get(rk);
    if (!e) { e = { keys: r.slice(0, nRow), byCol: new Map() }; rowMap.set(rk, e); rowOrder.push(rk); }
    e.byCol.set(ck, r[valStart]);
  }
  const rows = rowOrder.slice(0, MAX_ROWS).map((rk) => { const e = rowMap.get(rk)!; return { keys: e.keys, cells: colKeySet.map((ck) => (e.byCol.has(ck) ? e.byCol.get(ck) : null)) }; });
  return { rowFieldNames, colFieldNames, colKeys: colKeySet, colIsValues, rows, totalRows: rowOrder.length, truncatedCols: seenCol.size >= MAX_COLS, measureName };
}

// ── filter context for a cell ────────────────────────────────────────────────
export interface CtxEntry { field: string; value: string; src: 'rows' | 'cols' | 'filter' }
export interface CellContext { entries: CtxEntry[]; measure: string; value: string; calc: string }

const quote = (v: string) => (/^-?\d+(\.\d+)?$/.test(v) ? v : `"${v.replace(/"/g, '""')}"`);

export function cellContext(config: PivotConfig, m: Matrix, ri: number, ci: number): CellContext {
  const row = m.rows[ri];
  const entries: CtxEntry[] = [];
  const eqLines: string[] = [];
  config.rows.forEach((f, i) => { const v = fmtKey(row.keys[i]); entries.push({ field: f.name, value: v, src: 'rows' }); eqLines.push(`${f.ref} = ${quote(v)}`); });
  const measure = m.colIsValues ? m.colKeys[ci] : m.measureName;
  if (!m.colIsValues) {
    const parts = m.colKeys[ci].split(' / ');
    config.cols.forEach((f, i) => { const v = parts[i] ?? ''; entries.push({ field: f.name, value: v, src: 'cols' }); eqLines.push(`${f.ref} = ${quote(v)}`); });
  }
  config.filters.forEach((pf) => { const dax = filterToDax(pf); if (dax) eqLines.push(dax); });
  const filterDescr = config.filters.map((pf) => ({ field: pf.name, value: describeShort(pf), src: 'filter' as const }));
  entries.push(...filterDescr);
  const calc = `CALCULATE( [${measure}]${eqLines.length ? ',\n  ' + eqLines.join(',\n  ') : ''} )`;
  return { entries, measure, value: fmtVal(row.cells[ci]), calc };
}

function describeShort(pf: { filter: any }): string {
  const f = pf.filter;
  if (f.kind === 'date') return f.mode === 'rel' ? `last ${f.n} ${f.unit}` : f.mode === 'cal' ? f.period : 'range';
  if (f.kind === 'number') return f.op === 'between' ? `${f.a ?? '−∞'}–${f.b ?? '∞'}` : `${f.op} ${f.a}`;
  if (f.kind === 'text') return f.picked?.length ? f.picked.slice(0, 2).join(', ') : `${f.op} "${f.value}"`;
  return String(f.value);
}

// ── "Explain this number" payloads (feature #2) ─────────────────────────────
// A cell's pinned row/col keys become single-member filters (the engine's explain_value vocabulary — the
// probe_measure ProbeFilter shape) and the filter well's lines ride as raw DAX predicates, so the engine
// re-derives EXACTLY the number the cell shows.

/** Payload from a MATRIX cell (ri, ci index the tabulated matrix). groupBy carries the visual's AXIS
 * columns so the engine re-creates the SAME evaluation scope (ISINSCOPE / HASONEVALUE resolve as the
 * visual saw them — PR #92 review, Finding A); the single-member filters pin the clicked coordinates. */
export function explainPayloadFromMatrix(config: PivotConfig, m: Matrix, ri: number, ci: number): ExplainPayload {
  const ctx = cellContext(config, m, ri, ci);
  const row = m.rows[ri];
  const filters: { column: string; members: string[] }[] = [];
  config.rows.forEach((f, i) => filters.push({ column: f.ref, members: [fmtKey(row.keys[i])] }));
  if (!m.colIsValues) {
    const parts = m.colKeys[ci].split(' / ');
    config.cols.forEach((f, i) => filters.push({ column: f.ref, members: [parts[i] ?? ''] }));
  }
  return {
    measureName: ctx.measure,
    value: ctx.value,
    groupBy: [...config.rows, ...(m.colIsValues ? [] : config.cols)].map((f) => f.ref),
    filters,
    extraPredicates: config.filters.map(filterToDax).filter((x): x is string => !!x),
    entries: ctx.entries,
  };
}

/** Payload from a FLAT result row (the raw SUMMARIZECOLUMNS output: [rows…, cols…, values…]).
 * Returns null unless the clicked column IS a Values measure column — a right-click on a row/column
 * group cell must not silently explain the first measure (PR #92 review, Finding B). */
export function explainPayloadFromRow(config: PivotConfig, row: unknown[], ci: number): ExplainPayload | null {
  const nRow = config.rows.length, nCol = config.cols.length;
  if (!config.values.length || !row) return null;
  const valStart = nRow + nCol;
  if (ci < valStart || ci >= valStart + config.values.length) return null;   // not a value cell — no explanation exists
  const vIdx = ci - valStart;
  const measure = config.values[vIdx];
  if (!measure) return null;
  const filters: { column: string; members: string[] }[] = [];
  const entries: CtxEntry[] = [];
  config.rows.forEach((f, i) => { const v = fmtKey(row[i]); filters.push({ column: f.ref, members: [v] }); entries.push({ field: f.name, value: v, src: 'rows' }); });
  config.cols.forEach((f, i) => { const v = fmtKey(row[nRow + i]); filters.push({ column: f.ref, members: [v] }); entries.push({ field: f.name, value: v, src: 'cols' }); });
  config.filters.forEach((pf) => entries.push({ field: pf.name, value: describeShort(pf), src: 'filter' }));
  return {
    measureName: measure.name,
    value: fmtVal(row[ci]),
    groupBy: [...config.rows, ...config.cols].map((f) => f.ref),
    filters,
    extraPredicates: config.filters.map(filterToDax).filter((x): x is string => !!x),
    entries,
  };
}

// ── viz switcher ─────────────────────────────────────────────────────────────
const VIZ: { t: VizType; title: string; icon: React.ReactNode }[] = [
  { t: 'matrix', title: 'Matrix', icon: <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.2"><rect x="2" y="3" width="12" height="10" rx="1" /><path d="M2 6.2h12M6.2 6.2V13" /></svg> },
  { t: 'table', title: 'Table', icon: <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.2"><rect x="2" y="3" width="12" height="10" rx="1" /><path d="M2 6.5h12M2 9.7h12M6.5 3v10" /></svg> },
  { t: 'bar', title: 'Clustered bar', icon: <svg viewBox="0 0 16 16" fill="currentColor"><rect x="2" y="7" width="3" height="7" rx="1" /><rect x="6.5" y="4" width="3" height="10" rx="1" /><rect x="11" y="9" width="3" height="5" rx="1" /></svg> },
  { t: 'line', title: 'Line', icon: <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round"><path d="M2 12l3.5-4 3 2L14 3" /></svg> },
  { t: 'area', title: 'Area', icon: <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4"><path d="M2 12l3.5-4 3 2L14 4v9H2z" fill="currentColor" opacity=".28" /><path d="M2 12l3.5-4 3 2L14 4" /></svg> },
  { t: 'card', title: 'Card', icon: <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.2"><rect x="2" y="4" width="12" height="8" rx="1.4" /><path d="M4.5 8.5h4" strokeWidth="1.6" /></svg> },
  { t: 'scatter', title: 'Scatter', icon: <svg viewBox="0 0 16 16" fill="currentColor"><circle cx="4" cy="11" r="1.5" /><circle cx="8" cy="6" r="1.5" /><circle cx="12" cy="9" r="1.5" /><circle cx="6.5" cy="12.5" r="1.3" /></svg> },
];
export function VizSwitcher({ viz, onViz }: { viz: VizType; onViz: (v: VizType) => void }) {
  return (
    <div className="inline-flex gap-0.5 rounded-[9px] p-0.5" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      {VIZ.map((v) => (
        <button key={v.t} title={v.title} onClick={() => onViz(v.t)} className="grid place-items-center rounded-md" style={{ width: 30, height: 27, cursor: 'pointer', border: 'none', background: viz === v.t ? 'var(--sem-accent)' : 'transparent', color: viz === v.t ? '#fff' : 'var(--sem-muted)' }}>
          <span style={{ width: 15, height: 15, display: 'inline-flex' }}>{v.icon}</span>
        </button>
      ))}
    </div>
  );
}

// ── the visual ───────────────────────────────────────────────────────────────
export function DaxVisual({ res, config, viz, height, onExplain }: { res: ResultSet; config: PivotConfig; viz: VizType; height: number; onExplain?: (p: ExplainPayload) => void }) {
  const m = useMemo(() => tabulate(res, config), [res, config]);
  const [hover, setHover] = useState<{ ri: number; ci: number; x: number; y: number } | null>(null);

  if (!m) return <div className="flex items-center justify-center text-[12px] h-full" style={{ color: 'var(--sem-muted)' }}>Drag a measure into Values (and a field into Rows) to build a visual.</div>;
  if (viz === 'table') return <ResultGrid columns={res.columns} rows={res.rows} height={height}
    onCellMenu={onExplain ? (row, ci) => { const p = explainPayloadFromRow(config, row, ci); if (p) { onExplain(p); return true; } return false; } : undefined} />;
  if (viz === 'card') return <CardViz m={m} />;
  if (viz === 'matrix') return <MatrixViz m={m} config={config} height={height} hover={hover} setHover={setHover} onExplain={onExplain} />;
  return <ChartViz m={m} config={config} viz={viz} height={height} />;
}

function CardViz({ m }: { m: Matrix }) {
  // grand total of the first value across the matrix
  let total = 0, any = false;
  for (const r of m.rows) for (const c of r.cells) if (typeof c === 'number') { total += c; any = true; }
  return (
    <div className="flex flex-col items-center justify-center h-full gap-1">
      <div className="text-[11px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>{m.colIsValues ? m.colKeys[0] : m.measureName}</div>
          <div className="text-[40px] font-bold tnum" style={{ color: 'var(--sem-fg)' }}>{any ? total.toLocaleString(undefined, { maximumFractionDigits: 2 }) : 'No value'}</div>
      <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{m.totalRows} group{m.totalRows === 1 ? '' : 's'} · sum</div>
    </div>
  );
}

function MatrixViz({ m, config, height, hover, setHover, onExplain }: { m: Matrix; config: PivotConfig; height: number; hover: { ri: number; ci: number; x: number; y: number } | null; setHover: (h: { ri: number; ci: number; x: number; y: number } | null) => void; onExplain?: (p: ExplainPayload) => void }) {
  const ctx = hover ? cellContext(config, m, hover.ri, hover.ci) : null;
  return (
    <div className="overflow-auto rounded-lg h-full" style={{ border: '1px solid var(--sem-border)' }}>
      <table className="text-[12px] border-collapse">
        <thead><tr>
          {m.rowFieldNames.map((h, i) => <th key={'rh' + i} className="text-left px-2 py-1 font-semibold sticky top-0" style={{ background: 'var(--sem-surface-2)', borderBottom: '1px solid var(--sem-border)', position: 'sticky', left: 0, zIndex: 2 }}>{h}</th>)}
          {m.colKeys.map((ck, i) => <th key={'ck' + i} className="text-right px-2 py-1 font-semibold whitespace-nowrap sticky top-0" style={{ background: 'var(--sem-surface-2)', borderBottom: '1px solid var(--sem-border)' }}>{ck}</th>)}
        </tr></thead>
        <tbody>
          {m.rows.map((r, ri) => (
            <tr key={ri}>
              {r.keys.map((k, ki) => <td key={'k' + ki} className="px-2 py-1 whitespace-nowrap" style={{ borderBottom: '1px solid var(--sem-border)', background: 'var(--sem-surface)', position: 'sticky', left: 0 }}>{fmtKey(k)}</td>)}
              {r.cells.map((v, ci) => {
                const on = hover?.ri === ri && hover?.ci === ci;
                return <td key={'v' + ci} onMouseEnter={(e) => { const t = e.currentTarget.getBoundingClientRect(); setHover({ ri, ci, x: t.right, y: t.bottom }); }} onMouseLeave={() => setHover(null)}
                  onContextMenu={onExplain ? (e) => { e.preventDefault(); e.stopPropagation(); onExplain(explainPayloadFromMatrix(config, m, ri, ci)); } : undefined}
                  title={onExplain ? 'Right-click: explain this number' : undefined}
                  className="px-2 py-1 tnum text-right whitespace-nowrap cursor-default" style={{ borderBottom: '1px solid var(--sem-border)', color: v === null ? 'var(--sem-muted)' : 'var(--sem-fg)', background: on ? 'var(--sem-accent-soft)' : undefined, outline: on ? '1px solid var(--sem-accent)' : undefined }}>{v === null ? '·' : fmtVal(v)}</td>;
              })}
            </tr>
          ))}
        </tbody>
      </table>
      {ctx && hover && <CtxPopover ctx={ctx} x={hover.x} y={hover.y} />}
    </div>
  );
}

export function CtxPopover({ ctx, x, y }: { ctx: CellContext; x: number; y: number }) {
  const left = Math.max(8, Math.min(x + 6, (typeof window !== 'undefined' ? window.innerWidth : 1200) - 286));
  const top = Math.min(y + 4, (typeof window !== 'undefined' ? window.innerHeight : 800) - 240);
  const srcColor = { rows: '#9cdcfe', cols: '#9cdcfe', filter: 'var(--sem-accent)' } as const;
  return (
    <div className="fixed pointer-events-none" style={{ zIndex: 60, left, top, width: 278 }}>
      <div className="rounded-[11px] overflow-hidden" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-accent-line)', boxShadow: '0 18px 44px -20px rgba(0,0,0,.9)' }}>
        <div className="px-3 py-2 text-[10px] uppercase font-bold tracking-wide" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>Filter context · this point</div>
        <div className="px-3 py-2.5 flex flex-col gap-1.5">
          {ctx.entries.length === 0 && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>(no active filters, grand total)</div>}
          {ctx.entries.map((e, i) => (
            <div key={i} className="flex items-baseline gap-2 text-[12px]">
              <span style={{ color: srcColor[e.src], fontFamily: 'var(--mono, monospace)', fontSize: 11 }}>{e.field}</span>
              <span style={{ color: 'var(--sem-muted)' }}>=</span>
              <span style={{ color: e.src === 'filter' ? 'var(--sem-accent)' : 'var(--sem-fg)', fontWeight: 600 }}>{e.value}</span>
              <span className="ml-auto text-[9.5px] px-1.5 rounded" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>{e.src}</span>
            </div>
          ))}
          <div className="flex items-baseline gap-2 mt-1 pt-2 text-[13px]" style={{ borderTop: '1px solid var(--sem-border)' }}>
            <span style={{ color: 'var(--sem-accent)', fontFamily: 'var(--mono, monospace)', fontSize: 11.5 }}>[{ctx.measure}]</span>
            <span className="ml-auto font-bold tnum">{ctx.value}</span>
          </div>
          <pre className="text-[10.5px] rounded-md px-2 py-1.5 m-0 whitespace-pre-wrap" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-muted)', fontFamily: 'var(--mono, monospace)' }}>{ctx.calc}</pre>
        </div>
      </div>
    </div>
  );
}

function ChartViz({ m, config, viz, height }: { m: Matrix; config: PivotConfig; viz: VizType; height: number }) {
  const option = useMemo<echarts.EChartsCoreOption>(() => {
    const accent = cssVar('--sem-accent', '#2ED47A'), good = cssVar('--sem-good', '#36c98b'), warn = cssVar('--sem-warn', '#e0b341');
    const muted = cssVar('--sem-muted', '#9aa0aa'), border = cssVar('--sem-border', 'rgba(140,140,160,.22)'), fg = cssVar('--sem-fg', '#ddd');
    const palette = [accent, good, warn, '#4ec9b0', '#c586c0', '#9cdcfe', '#e06c75'];
    const categories = m.rows.map((r) => r.keys.map(fmtKey).join(' / '));
    const seriesNames = m.colKeys;
    const base = viz === 'scatter' ? 'scatter' : viz === 'line' || viz === 'area' ? 'line' : 'bar';
    const series = seriesNames.map((sn, si) => ({
      name: sn, type: base,
      ...(viz === 'area' ? { areaStyle: { opacity: 0.18 }, smooth: true } : {}),
      ...(viz === 'line' ? { smooth: true, symbol: 'circle', symbolSize: 5 } : {}),
      itemStyle: { color: palette[si % palette.length], borderRadius: base === 'bar' ? [3, 3, 0, 0] : 0 },
      data: viz === 'scatter' ? m.rows.map((r, ri) => [ri, typeof r.cells[si] === 'number' ? r.cells[si] : null]) : m.rows.map((r) => (typeof r.cells[si] === 'number' ? r.cells[si] : null)),
    }));
    const showLegend = seriesNames.length > 1 && !m.colIsValues;
    return {
      grid: { left: 52, right: 14, top: 16, bottom: showLegend ? 38 : 24 },
      legend: { show: showLegend, type: 'scroll', bottom: 0, textStyle: { color: muted, fontSize: 10 }, icon: 'roundRect' },
      tooltip: {
        trigger: 'item', borderColor: border, backgroundColor: cssVar('--sem-surface', '#26263a'), textStyle: { color: fg, fontSize: 11 },
        formatter: (p: any) => {
          const ci = viz === 'scatter' ? (p.data?.[0] ?? p.dataIndex) : p.dataIndex;
          const ctx = cellContext(config, m, ci, p.seriesIndex);
          const lines = ctx.entries.map((e) => `<div style="color:${muted}">${esc(e.field)} = <b style="color:${e.src === 'filter' ? accent : fg}">${esc(e.value)}</b> <span style="opacity:.6">(${e.src})</span></div>`).join('');
          return `<div style="font-weight:700;color:${muted};font-size:10px;text-transform:uppercase;letter-spacing:.06em;margin-bottom:4px">filter context</div>${lines || `<div style="color:${muted}">grand total</div>`}<div style="margin-top:5px;padding-top:5px;border-top:1px solid ${border}"><b style="color:${accent}">[${esc(ctx.measure)}]</b> = <b>${esc(ctx.value)}</b></div>`;
        },
      },
      xAxis: { type: 'category', data: categories, axisLine: { lineStyle: { color: border } }, axisLabel: { color: muted, fontSize: 10, interval: 0, hideOverlap: true } },
      yAxis: { type: 'value', axisLabel: { color: muted, fontSize: 10 }, splitLine: { lineStyle: { color: border } } },
      series,
    };
  }, [m, config, viz]);
  return <EChart option={option} height={height} />;
}
