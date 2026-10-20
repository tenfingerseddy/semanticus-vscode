// DAX Lab's visual surface: render the run result as a matrix or an ECharts visual, and — the headline —
// show the FILTER CONTEXT behind any data point on hover (the row/column keys + active filters, tagged by
// source, plus the equivalent CALCULATE). All derived client-side from the result + the wells; no extra
// engine calls.

import { useMemo, useState } from 'react';
import * as echarts from 'echarts';
import { EChart, tokenPx } from './echart';
import { seriesColor, seriesPalette, useThemeEpoch } from './chartpalette';
import { ResultGrid } from './grid';
import type { ResultSet } from './wire';
import { PivotConfig, contextFilterLines, queryGroupRefs, sortPlan } from './daxwells';
import { axisDims, timeKey, type TimeKey } from './axisorder';
import { axisLabelText } from './periodorder.mjs';
import { offeredKinds, twoSplitAxes, scatterPoints } from './chartpick.mjs';
import type { ExplainPayload } from './explainpanel';
import { columnFormatter, friendlyHeader, plainNumber } from './resultformat.mjs';
import { percentOf, formatNumber } from './gridformat';

export type VizType = 'matrix' | 'table' | 'bar' | 'line' | 'area' | 'card' | 'scatter';

const fmtKey = (v: unknown) => (v === null || v === undefined ? '' : String(v));
/** A number as it reads on screen. The measure's own percent format shows 0.0068 as 0.7% (a percent answer never turns
 *  into a rounded 0.01); any other format, or none, keeps the plain number. */
const fmtVal = (v: unknown, format?: string | null) => (v === null || v === undefined ? '' : typeof v === 'number' ? (formatNumber(v, format) ?? plainNumber(v)) : String(v));

function cssVar(n: string, f: string) { const v = getComputedStyle(document.documentElement).getPropertyValue(n).trim(); return v || f; }
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => (c === '&' ? '&amp;' : c === '<' ? '&lt;' : c === '>' ? '&gt;' : '&quot;'));

import { tabulate, type Matrix } from './tabulate';
export { tabulate };
/** The format of the number in one matrix cell column: each measure's own when the columns are the measures, else the first's. */
const formatAt = (m: Matrix, ci: number) => (m.colIsValues ? m.formats[ci] : m.formats[0]);

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
  eqLines.push(...sortByLines(config, m, [...config.rows.map((_, i) => fmtKey(row.keys[i])), ...(m.colIsValues ? [] : m.colKeys[ci].split(' / '))]));
  eqLines.push(...contextFilterLines(config));
  entries.push(...filterEntries(config));
  const calc = `CALCULATE( [${measure}]${eqLines.length ? ',\n  ' + eqLines.join(',\n  ') : ''} )`;
  return { entries, measure, value: fmtVal(row.cells[ci], formatAt(m, ci)), calc };
}

/** The sort-by columns the drawn query grouped by are in the cell's filter context too, so the cell's own calculation pins
 *  them at the value that label has in the model's order. A sort-by the result did not carry has no value to pin. */
function sortByLines(config: PivotConfig, m: Matrix, labels: string[]): string[] {
  const { group, extra, source } = sortPlan(config);
  const out: string[] = [];
  group.forEach((_, i) => {
    const at = source[i];
    if (at === null || at === undefined || at < group.length) return;   // no sort-by, or it is a well field already pinned
    const v = m.sortRank?.[i]?.get(labels[i] ?? '');
    if (v !== undefined && v !== null) out.push(`${extra[at - group.length].ref} = ${quote(String(v))}`);
  });
  return [...new Set(out)];
}

// The filter well's entries, then the context the wells cannot hold (a question's filters).
function filterEntries(config: PivotConfig): CtxEntry[] {
  return [...config.filters.map((pf) => ({ field: pf.name, value: describeShort(pf), src: 'filter' as const })),
    ...(config.context ?? []).map((l) => ({ field: l.name, value: l.value, src: 'filter' as const }))];
}

function describeShort(pf: { filter: any }): string {
  const f = pf.filter;
  if (f.kind === 'date') return f.mode === 'rel' ? `last ${f.n} ${f.unit}` : f.mode === 'cal' ? f.period : 'range';
  if (f.kind === 'number') return f.op === 'between' ? `${f.a ?? '−∞'} to ${f.b ?? '∞'}` : `${f.op} ${f.a}`;
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
    groupBy: queryGroupRefs(config),
    filters,
    extraPredicates: contextFilterLines(config),
    entries: ctx.entries,
    format: formatAt(m, ci),
  };
}

/** Payload from a FLAT result row (the raw SUMMARIZECOLUMNS output: [rows…, cols…, values…]).
 * Returns null unless the clicked column IS a Values measure column — a right-click on a row/column
 * group cell must not silently explain the first measure (PR #92 review, Finding B). */
export function explainPayloadFromRow(config: PivotConfig, row: unknown[], ci: number, format?: string | null): ExplainPayload | null {
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
  entries.push(...filterEntries(config));
  return {
    measureName: measure.name,
    value: fmtVal(row[ci], format),
    groupBy: queryGroupRefs(config),
    filters,
    extraPredicates: contextFilterLines(config),
    entries,
    format,
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
/** Only the kinds the result fits are shown: a button that does nothing for this result is not on the row (K22). */
export function VizSwitcher({ viz, onViz, fits }: { viz: VizType; onViz: (v: VizType) => void; fits?: VizType[] }) {
  return (
    // Borderless segment items, no box around them: the chart kinds are one row of toggles in the build card.
    <div className="flex items-center" role="group" aria-label="Chart kind">
      {VIZ.filter((v) => !fits || fits.includes(v.t)).map((v) => (
        <button key={v.t} title={v.title} aria-label={v.title} aria-pressed={viz === v.t}
          onClick={() => onViz(v.t)} className="sem-seg-item" style={{ width: 'var(--sem-control-h)' }}>
          <span style={{ width: 15, height: 15, display: 'inline-flex' }}>{v.icon}</span>
        </button>
      ))}
    </div>
  );
}

// ── the visual ───────────────────────────────────────────────────────────────
// `across`: the columns (not the rows) are the axis a line or bar runs along. Queries sets it when the date sits in
// the columns and the rows are categories, so time runs left to right and each row is one series (doc 03, P2).
// `chosenOrder`: the person put the fields in their places (Queries' wells), so two splits keep that order. An answer in Ask your
// data leaves it off, and a bar puts the field with more values on the axis (twoSplitAxes).
export function DaxVisual({ res, config, viz, height, onExplain, across = false, chosenOrder = false }: { res: ResultSet; config: PivotConfig; viz: VizType; height: number; onExplain?: (p: ExplainPayload) => void; across?: boolean; chosenOrder?: boolean }) {
  // One measure by two columns is drawn as a series for each value of one column along the other (twoSplitAxes), so a chart
  // reads it like the pivot it is; the grid, the matrix and the card keep the config as the person built it.
  const isChart = viz !== 'table' && viz !== 'card' && viz !== 'matrix';
  const fold = useMemo(() => {
    if (!isChart || config.cols.length || config.rows.length !== 2 || config.values.length !== 1) return null;
    const at = twoSplitAxes(axisDims(res, config), 1, viz, chosenOrder);
    return at ? { config: { ...config, rows: [config.rows[0]], cols: [config.rows[1]] }, across: at.axis === 1 } : null;
  }, [res, config, viz, isChart, chosenOrder]);
  const cfg = fold?.config ?? config;
  const m = useMemo(() => tabulate(res, cfg), [res, cfg]);
  // On screen a key reads as a person would write it ("Jan 2025", "3 Jan 2025"); the raw key still feeds every filter.
  const fmt = useMemo(() => {
    const byCol = res.columns.map((c, ci) => columnFormatter(res.rows, ci, c.name));
    const nRow = cfg.rows.length;
    return {
      rowKey: (ki: number, v: unknown) => (byCol[ki] ?? String)(v),
      colKey: (ck: string) => ck.split(' / ').map((part, i) => { const f = byCol[nRow + i]; if (!f) return part; const raw = res.rows.find((r) => String(r[nRow + i] ?? '') === part)?.[nRow + i]; return f(raw ?? part); }).join(' / '),
    };
  }, [res, cfg]);
  const [hover, setHover] = useState<{ ri: number; ci: number; x: number; y: number } | null>(null);

  if (!m) return <div className="flex items-center justify-center text-xs h-full" style={{ color: 'var(--sem-muted)' }}>Drag a measure into Values (and a field into Rows) to build a visual.</div>;
  if (viz === 'table') return <ResultGrid columns={res.columns} rows={res.rows} height={height} friendly
    onCellMenu={onExplain ? (row, ci) => { const p = explainPayloadFromRow(config, row, ci, res.columns[ci]?.format); if (p) { onExplain(p); return true; } return false; } : undefined} />;
  if (viz === 'card') return <CardViz m={m} />;
  if (viz === 'matrix') return <MatrixViz m={m} config={config} height={height} hover={hover} setHover={setHover} onExplain={onExplain} fmt={fmt} />;
  return <ChartViz m={m} config={cfg} viz={viz} height={height} across={(fold?.across ?? across) && !m.colIsValues} fmt={fmt} seriesOrdered={seriesOrdered(res, cfg, (fold?.across ?? across) && !m.colIsValues, m.colIsValues)} />;
}

// A card is ONE number. Added together, two measures (Net Sales 1,000 and Units 10) make a figure nobody asked for, and so do
// the rows of one measure (two regions' return rates, an average per row): the card draws a number only when there is exactly
// one, and otherwise says why it shows none (Sol 3.6).
function CardViz({ m }: { m: Matrix }) {
  const single = m.colIsValues && m.colKeys.length === 1 && m.rows.length === 1 && m.rows[0].cells.length === 1 && m.totalRows === 1;
  const value = single ? m.rows[0].cells[0] : null;
  return (
    <div className="flex flex-col items-center justify-center h-full gap-1" data-testid="card-viz">
      <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{m.colIsValues ? m.colKeys[0] : m.measureName}</div>
      {single
        ? <div className="text-(length:--sem-text-page) font-bold tnum" style={{ color: 'var(--sem-fg)' }}>{typeof value === 'number' ? fmtVal(value, formatAt(m, 0)) : 'No value'}</div>
        : <div className="text-(length:--sem-text-label) text-center px-3" style={{ color: 'var(--sem-muted)' }}>A card shows one number, and this result has more than one. Pick another chart.</div>}
    </div>
  );
}

type KeyFmt = { rowKey: (ki: number, v: unknown) => string; colKey: (ck: string) => string };
function MatrixViz({ m, config, height, hover, setHover, onExplain, fmt }: { fmt: KeyFmt; m: Matrix; config: PivotConfig; height: number; hover: { ri: number; ci: number; x: number; y: number } | null; setHover: (h: { ri: number; ci: number; x: number; y: number } | null) => void; onExplain?: (p: ExplainPayload) => void }) {
  const ctx = hover ? cellContext(config, m, hover.ri, hover.ci) : null;
  return (
    <div className="overflow-auto rounded-(--sem-radius-card) h-full" style={{ border: '1px solid var(--sem-border)' }}>
      {/* Rows keep their own height: stretched to the card they were very tall (UAT 17 Oct, F28). */}
      <table className="text-xs border-collapse w-full">
        <thead><tr style={{ height: 'var(--sem-row)' }}>
          {m.rowFieldNames.map(friendlyHeader).map((h, i) => <th key={'rh' + i} className="text-left px-2 py-1 font-semibold sticky top-0" style={{ background: 'var(--sem-ground)', borderBottom: '1px solid var(--sem-rule)', position: 'sticky', left: 0, zIndex: 2 }}>{h}</th>)}
          {m.colKeys.map((ck, i) => <th key={'ck' + i} className="text-right px-2 py-1 font-semibold whitespace-nowrap sticky top-0" style={{ background: 'var(--sem-ground)', borderBottom: '1px solid var(--sem-rule)' }}>{m.colIsValues ? ck : fmt.colKey(ck)}</th>)}
        </tr></thead>
        <tbody>
          {m.rows.map((r, ri) => (
            <tr key={ri}>
              {r.keys.map((k, ki) => <td key={'k' + ki} className="px-2 py-1 whitespace-nowrap" style={{ borderBottom: '1px solid var(--sem-border)', background: 'var(--sem-surface)', position: 'sticky', left: 0 }}>{fmt.rowKey(ki, k)}</td>)}
              {r.cells.map((v, ci) => {
                const on = hover?.ri === ri && hover?.ci === ci;
                return <td key={'v' + ci} onMouseEnter={(e) => { const t = e.currentTarget.getBoundingClientRect(); setHover({ ri, ci, x: t.right, y: t.bottom }); }} onMouseLeave={() => setHover(null)}
                  onContextMenu={onExplain ? (e) => { e.preventDefault(); e.stopPropagation(); onExplain(explainPayloadFromMatrix(config, m, ri, ci)); } : undefined}
                  title={onExplain ? 'Right-click: explain this number' : undefined}
                  className="px-2 py-1 tnum text-right whitespace-nowrap cursor-default" style={{ borderBottom: '1px solid var(--sem-border)', color: v === null ? 'var(--sem-muted)' : 'var(--sem-fg)', background: on ? 'var(--sem-accent-soft)' : undefined, outline: on ? '1px solid var(--sem-accent)' : undefined }}>{v === null ? '·' : fmtVal(v, formatAt(m, ci))}</td>;
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
  const srcColor = { rows: 'var(--sem-series-2)', cols: 'var(--sem-series-2)', filter: 'var(--sem-series-1)' } as const;
  return (
    <div className="fixed pointer-events-none" style={{ zIndex: 60, left, top, width: 278 }}>
      <div className="rounded-(--sem-radius-card) overflow-hidden" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-accent-line)', boxShadow: '0 18px 44px -20px rgba(0,0,0,.9)' }}>
        <div className="px-3 py-2 text-(length:--sem-text-label) font-bold" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>Filter context · this point</div>
        <div className="px-3 py-2 flex flex-col gap-2">
          {ctx.entries.length === 0 && <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>(no active filters, grand total)</div>}
          {ctx.entries.map((e, i) => (
            <div key={i} className="flex items-baseline gap-2 text-xs">
              <span style={{ color: srcColor[e.src], fontFamily: 'var(--mono, monospace)', fontSize: 'var(--sem-text-label)' }}>{e.field}</span>
              <span style={{ color: 'var(--sem-muted)' }}>=</span>
              <span style={{ color: e.src === 'filter' ? 'var(--sem-accent)' : 'var(--sem-fg)', fontWeight: 600 }}>{e.value}</span>
              <span className="ml-auto text-(length:--sem-text-label) px-2 rounded" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>{e.src}</span>
            </div>
          ))}
          <div className="flex items-baseline gap-2 mt-1 pt-2 text-(length:--sem-text-body)" style={{ borderTop: '1px solid var(--sem-border)' }}>
            <span style={{ fontFamily: 'var(--mono, monospace)', fontSize: 'var(--sem-text-meta)' }}>[{ctx.measure}]</span>
            <span className="ml-auto font-bold tnum">{ctx.value}</span>
          </div>
          <pre className="text-(length:--sem-text-label) rounded-(--sem-radius-control) px-2 py-1 m-0 whitespace-pre-wrap" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-muted)', fontFamily: 'var(--mono, monospace)' }}>{ctx.calc}</pre>
        </div>
      </div>
    </div>
  );
}

/** Pixels under the plot when a legend is drawn: the axis labels, then the legend, with a little air between them. */
export const LEGEND_ROOM = 58;

/** Whether the series of a chart have an order (months, days, years, numbers): then a chart with many of them draws a ramp that
 *  reads in that order, and otherwise the six steps and then a ramp. Series from a measure, or from joined fields, have none. */
function seriesOrdered(res: ResultSet, cfg: PivotConfig, across: boolean, colIsValues: boolean): boolean {
  if (colIsValues) return false;
  const fields = across ? cfg.rows : cfg.cols;
  if (fields.length !== 1) return false;
  const ci = across ? 0 : cfg.rows.length;
  const dim = axisDims(res, cfg)[ci];
  if (dim?.isDate) return dim.ordered !== false;
  return res.rows.length > 0 && res.rows.every((r) => r?.[ci] == null || typeof r[ci] === 'number');
}

function ChartViz({ m, config, viz, height, across = false, fmt, seriesOrdered: ordered = false }: { fmt: KeyFmt; m: Matrix; config: PivotConfig; viz: VizType; height: number; across?: boolean; seriesOrdered?: boolean }) {
  const theme = useThemeEpoch();
  const option = useMemo<echarts.EChartsCoreOption>(() => {
    const accent = seriesColor(0);
    const muted = cssVar('--sem-muted', '#9aa0aa'), border = cssVar('--sem-border', 'rgba(140,140,160,.22)'), fg = cssVar('--sem-fg', '#ddd');
    // The chart palette (tokens.css): green, blue, purple, then each one step further from the page; past six, a ramp so no colour
    // repeats (chartpalette.ts). A series also has its legend name and its place, so colour is never the only way to tell two apart.
    const rowNames = m.rows.map((r) => r.keys.map((k, ki) => fmt.rowKey(ki, k)).join(' / '));
    const colNames = m.colIsValues ? m.colKeys : m.colKeys.map(fmt.colKey);
    const categories = across ? colNames : rowNames;
    const seriesNames = across ? rowNames : colNames;
    const palette = seriesPalette(Math.max(seriesNames.length, 3), ordered);
    // cell(series, point) -> the matrix cell, whichever way round the chart is drawn
    const cellAt = (si: number, pi: number) => (across ? m.rows[si]?.cells[pi] : m.rows[pi]?.cells[si]);
    const points = across ? m.colKeys.length : m.rows.length;
    const base = viz === 'scatter' ? 'scatter' : viz === 'line' || viz === 'area' ? 'line' : 'bar';
    // A scatter sets one measure against the other, one point for each row; it is not a measure against the row's position.
    if (viz === 'scatter' && m.colIsValues && m.colKeys.length >= 2) {
      const pts = scatterPoints(rowNames, m.rows.map((r) => r.cells));
      const rowAt = new Map(rowNames.map((n, i) => [n, i]));
      const tick = (ci: number) => (v: number) => fmtVal(v, m.formats[ci]);
      const axisName = (ci: number) => ({ name: m.colKeys[ci], nameLocation: 'middle' as const, nameGap: ci === 0 ? 30 : 42, nameTextStyle: { color: muted, fontSize: tokenPx('--sem-text-label') } });
      return {
        grid: { left: 64, right: 14, top: 16, bottom: 40 },
        legend: { show: false },
        tooltip: {
          trigger: 'item', borderColor: border, backgroundColor: cssVar('--sem-surface', '#26263a'), textStyle: { color: fg, fontSize: tokenPx('--sem-text-label') },
          formatter: (p: any) => {
            const ri = rowAt.get(p.data?.[2]) ?? 0;
            const ctx = cellContext(config, m, ri, 0);
            const lines = ctx.entries.map((e) => `<div style="color:${muted}">${esc(e.field)} = <b style="color:${e.src === 'filter' ? accent : fg}">${esc(e.value)}</b></div>`).join('');
            const both = [0, 1].map((ci) => `<div><b style="color:${accent}">[${esc(m.colKeys[ci])}]</b> = <b>${esc(fmtVal(m.rows[ri]?.cells[ci], m.formats[ci]))}</b></div>`).join('');
            return `${lines}<div style="margin-top:5px;padding-top:5px;border-top:1px solid ${border}">${both}</div>`;
          },
        },
        xAxis: { type: 'value', scale: true, ...axisName(0), axisLine: { lineStyle: { color: border } }, axisLabel: { color: muted, fontSize: tokenPx('--sem-text-label'), formatter: tick(0) }, splitLine: { lineStyle: { color: border } } },
        yAxis: { type: 'value', scale: true, ...axisName(1), axisLabel: { color: muted, fontSize: tokenPx('--sem-text-label'), formatter: tick(1) }, splitLine: { lineStyle: { color: border } } },
        series: [{ type: 'scatter', symbolSize: 9, itemStyle: { color: palette[0] }, data: pts.map((q) => [q.x, q.y, q.name]) }],
      };
    }
    const series = seriesNames.map((sn, si) => ({
      name: sn, type: base,
      ...(viz === 'area' ? { areaStyle: { opacity: 0.18 }, smooth: true } : {}),
      ...(viz === 'line' ? { smooth: false, symbol: 'circle', symbolSize: 5 } : {}),
      itemStyle: { color: palette[si] ?? palette[si % palette.length], borderRadius: base === 'bar' ? [3, 3, 0, 0] : 0 },
      data: Array.from({ length: points }, (_, pi) => { const v = cellAt(si, pi); const n = typeof v === 'number' ? v : null; return viz === 'scatter' ? [pi, n] : n; }),
    }));
    const shown = new Set(m.formats);
    const axisFormat = shown.size === 1 && m.formats[0] && percentOf(1, m.formats[0]) ? m.formats[0] : null;
    const showLegend = seriesNames.length > 1 && (across || !m.colIsValues);
    return {
      // The axis labels hang below the plot (about 23px at the label size) and the legend sits under them at the bottom edge (about 25px), so the plot leaves
      // room for both: at 38 the legend drew over the last month's label (join 6, "Dec" at 1440x900).
      grid: { left: 52, right: 14, top: 16, bottom: showLegend ? LEGEND_ROOM : 24 },
      legend: { show: showLegend, type: 'scroll', bottom: 0, textStyle: { color: muted, fontSize: tokenPx('--sem-text-label') }, icon: 'roundRect' },
      tooltip: {
        trigger: 'item', borderColor: border, backgroundColor: cssVar('--sem-surface', '#26263a'), textStyle: { color: fg, fontSize: tokenPx('--sem-text-label') },
        formatter: (p: any) => {
          const ci = viz === 'scatter' ? (p.data?.[0] ?? p.dataIndex) : p.dataIndex;
          const ctx = across ? cellContext(config, m, p.seriesIndex, ci) : cellContext(config, m, ci, p.seriesIndex);
          const lines = ctx.entries.map((e) => `<div style="color:${muted}">${esc(e.field)} = <b style="color:${e.src === 'filter' ? accent : fg}">${esc(e.value)}</b> <span style="opacity:.6">(${e.src})</span></div>`).join('');
          return `<div style="font-weight:700;color:${muted};font-size:10px;text-transform:uppercase;letter-spacing:.06em;margin-bottom:4px">filter context</div>${lines || `<div style="color:${muted}">grand total</div>`}<div style="margin-top:5px;padding-top:5px;border-top:1px solid ${border}"><b style="color:${accent}">[${esc(ctx.measure)}]</b> = <b>${esc(ctx.value)}</b></div>`;
        },
      },
      xAxis: { type: 'category', data: categories, axisLine: { lineStyle: { color: border } }, axisLabel: { color: muted, fontSize: tokenPx('--sem-text-label'), interval: 0, hideOverlap: true, formatter: (v: string) => axisLabelText(v) } },
      // One percent format on every series puts the axis in percents too, so a bar's height and its tooltip agree.
      yAxis: { type: 'value', axisLabel: { color: muted, fontSize: tokenPx('--sem-text-label'), ...(axisFormat ? { formatter: (v: number) => percentOf(v, axisFormat) ?? String(v) } : {}) }, splitLine: { lineStyle: { color: border } } },
      series,
    };
  }, [m, config, viz, across, fmt, theme, ordered]);
  return <EChart option={option} height={height} />;
}
