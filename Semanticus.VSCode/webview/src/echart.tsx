import { useEffect, useMemo, useRef } from 'react';
import * as echarts from 'echarts';
import 'echarts/theme/v5.js';

// Test-only hook: the uishot interaction driver (tools/uishot/lineage-interact.mjs) needs the echarts namespace to
// resolve a canvas chart instance (getInstanceByDom) and compute a graph node's on-screen pixel — so it can issue a
// REAL mouse click at a node and exercise the click→pin→walk wiring headlessly. Guarded on a flag the driver sets
// before load; in production window.__ECHARTS_TEST__ is undefined, so this is a single no-op check at module init.
if (typeof window !== 'undefined' && (window as { __ECHARTS_TEST__?: unknown }).__ECHARTS_TEST__) {
  (window as { __ECHARTS__?: typeof echarts }).__ECHARTS__ = echarts;
}

function cssVar(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

// The treemap tooltip is rendered by ECharts as innerHTML; model names are interpolated into it,
// so escape them. (The nonce CSP already blocks script execution — this is defense-in-depth.)
function esc(s: string): string {
  return s.replace(/[&<>"]/g, (c) => (c === '&' ? '&amp;' : c === '<' ? '&lt;' : c === '>' ? '&gt;' : '&quot;'));
}

/** Minimal reusable ECharts mount: owns init/setOption/resize/dispose for a sized container. Optional onEvents binds
 *  chart events (e.g. {click, mouseover}) once at mount and delegates through a live ref so handler closures stay
 *  current without rebinding; onChartReady hands back the instance for imperative dispatchAction (search/focus). */
export function EChart({ option, height, className, onEvents, onChartReady }: {
  option: echarts.EChartsCoreOption; height: number; className?: string;
  onEvents?: Record<string, (params: any) => void>;
  onChartReady?: (chart: echarts.ECharts) => void;
}) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const handlers = useRef(onEvents); handlers.current = onEvents;
  const ready = useRef(onChartReady); ready.current = onChartReady;

  useEffect(() => {
    if (!ref.current) return;
    const c = echarts.init(ref.current, 'v5', { renderer: 'canvas' });
    chart.current = c;
    const ro = new ResizeObserver(() => c.resize());
    ro.observe(ref.current);
    for (const name of Object.keys(handlers.current ?? {})) c.on(name, (p: any) => handlers.current?.[name]?.(p));
    ready.current?.(c);
    return () => { ro.disconnect(); c.dispose(); chart.current = null; };
  }, []);

  useEffect(() => { chart.current?.setOption(option, true); }, [option]);

  return <div ref={ref} className={className} style={{ height, width: '100%' }} />;
}

// ---- VertiPaq storage treemap ---------------------------------------------------------------
export interface VpaqColumn { table: string; column: string; totalSize: number; encoding: string; pctOfModel: number; dataSize?: number; dictionarySize?: number; hashIndexSize?: number; }
export interface VpaqTable { name: string; size: number; rows: number | null; pctOfModel: number; columns?: number; }

const ENCODING_COLOR: Record<string, string> = { Hash: '#7C8BA5', Value: '#36c98b', RLE: '#e0b341' };

export function VpaqTreemap({ tables, columns }: { tables: VpaqTable[]; columns: VpaqColumn[] }) {
  const accent = cssVar('--sem-accent', '#2ED47A');
  const fg = cssVar('--sem-fg', '#ddd');
  const border = cssVar('--sem-bg', '#1e1e1e');

  const option = useMemo<echarts.EChartsCoreOption>(() => {
    const byTable = new Map<string, VpaqColumn[]>();
    for (const c of columns) { const a = byTable.get(c.table) ?? []; a.push(c); byTable.set(c.table, a); }

    const data = tables.map((t) => {
      const cols = (byTable.get(t.name) ?? []).slice().sort((a, b) => b.totalSize - a.totalSize);
      const known = cols.reduce((s, c) => s + c.totalSize, 0);
      const children = cols.map((c) => ({
        name: c.column,
        value: c.totalSize,
        itemStyle: { color: ENCODING_COLOR[c.encoding] ?? accent },
      }));
      // The DMV gives us only the biggest columns; show the remainder so table areas stay true-to-size.
      const other = Math.max(0, t.size - known);
      if (other > t.size * 0.02 && children.length > 0) {
        children.push({ name: '(other columns)', value: other, itemStyle: { color: 'rgba(140,140,160,0.35)' } });
      }
      return { name: t.name, value: t.size, children: children.length ? children : undefined };
    });

    const fmt = (b: number) => (b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : (b / 1024).toFixed(0) + ' KB');

    return {
      tooltip: {
        formatter: (info: any) => {
          const v = info.value as number;
          const path = (info.treePathInfo ?? []).map((p: any) => p.name).filter(Boolean).map((s: string) => esc(s)).join(' › ');
          return `<b>${path}</b><br/>${fmt(v)}`;
        },
      },
      series: [{
        type: 'treemap',
        roam: false,
        nodeClick: 'zoomToNode',
        breadcrumb: { show: true, height: 22, bottom: 0, itemStyle: { color: accent, textStyle: { color: '#fff' } } },
        label: { show: true, color: '#fff', fontSize: 11, overflow: 'truncate' },
        upperLabel: { show: true, height: 20, color: fg, fontSize: 11 },
        itemStyle: { borderColor: border, borderWidth: 1, gapWidth: 1 },
        levels: [
          { itemStyle: { borderColor: border, borderWidth: 3, gapWidth: 3 }, upperLabel: { show: true } },
          { itemStyle: { borderColorSaturation: 0.5, gapWidth: 1, borderWidth: 1 } },
        ],
        data,
      }],
    };
  }, [tables, columns, accent, fg, border]);

  return <EChart option={option} height={420} />;
}

// ---- VertiPaq ranked component bars ----------------------------------------------------------
// The three storage components a column pays for. Same palette as the header composition cards so the
// legend, the cards, and the bar segments read as one system. Fixed hex (like the treemap) — distinct
// and legible in both light and dark themes.
export const VPAQ_COMPONENT_COLORS = { data: '#3fa66d', dict: '#5b8ff9', hash: '#e0a53d' };
// The neutral remainder shade for a table bar's bytes NOT covered by the scanned columns' component split —
// the table total is exact but the split is partial, and the gap must be visible, never silently absorbed.
export const VPAQ_UNATTRIBUTED_COLOR = 'rgba(140,140,160,0.45)';

export interface VpaqBarItem {
  ref: string; label: string; sublabel?: string;
  data: number; dict: number; hash: number; total: number;
  unattributed?: number;   // tables mode only: total minus the scanned columns' component sum (>=0)
  pctModel: number; pctTable: number;
}

/** Ranked horizontal stacked bars: the top storage consumers, each split into data / dictionary /
 *  hash-index bytes (+ an explicit neutral "unattributed" remainder when the split does not cover the
 *  whole total — tables mode). Length (not area) is the ranking signal, and the stack exposes WHY an
 *  object is big. Clicking a bar calls onSelect with the object ref (the caller selects + filters).
 *  Storage mode drives the tooltip vocabulary: resident-only and unknown observations must never be
 *  presented as a percentage of the full model. */
export function VpaqComponentBars({ items, storageMode = 'unknown', onSelect }: { items: VpaqBarItem[]; storageMode?: 'import' | 'directLake' | 'unknown'; onSelect?: (ref: string) => void }) {
  const muted = cssVar('--sem-muted', '#9aa0aa');
  const fg = cssVar('--sem-fg', '#ddd');
  const border = cssVar('--sem-border', 'rgba(140,140,160,0.22)');
  const rows = useMemo(() => items.slice(0, 12), [items]);
  const C = VPAQ_COMPONENT_COLORS;

  const option = useMemo<echarts.EChartsCoreOption>(() => {
    const fmt = (b: number) => (b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : (b / 1024).toFixed(0) + ' KB');
    const seg = (name: string, color: string, pick: (r: VpaqBarItem) => number) => ({
      name, type: 'bar', stack: 'components', barMaxWidth: 20,
      itemStyle: { color }, emphasis: { focus: 'series' }, data: rows.map(pick),
    });
    const hasUnattributed = rows.some((r) => (r.unattributed ?? 0) > 0);
    const series = [seg('Data', C.data, (r) => r.data), seg('Dictionary', C.dict, (r) => r.dict), seg('Hash indexes', C.hash, (r) => r.hash)];
    if (hasUnattributed) series.push(seg('Unattributed', VPAQ_UNATTRIBUTED_COLOR, (r) => r.unattributed ?? 0));
    return {
      grid: { left: 4, right: 64, top: 6, bottom: 6, containLabel: true },
      tooltip: {
        trigger: 'axis', axisPointer: { type: 'shadow' },
        formatter: (ps: any) => {
          const it = rows[ps?.[0]?.dataIndex ?? 0]; if (!it) return '';
          const sub = it.sublabel ? ` <span style="opacity:.65">${esc(it.sublabel)}</span>` : '';
          const rem = (it.unattributed ?? 0) > 0
            ? `<br/><span style="color:${VPAQ_UNATTRIBUTED_COLOR}">■</span> Unattributed ${fmt(it.unattributed as number)} (not covered by the scanned columns)`
            : '';
          const shares = storageMode === 'directLake'
            ? `${fmt(it.total)} resident · ${it.pctModel}% of resident storage · ${it.pctTable}% of its table's resident storage`
            : storageMode === 'unknown'
              ? `${fmt(it.total)} observed · ${it.pctModel}% of observed storage · ${it.pctTable}% of its table's observed storage`
              : `${fmt(it.total)} · ${it.pctModel}% of model · ${it.pctTable}% of table`;
          return `<b>${esc(it.label)}</b>${sub}<br/>${shares}<br/>`
            + `<span style="color:${C.data}">■</span> Data ${fmt(it.data)} &nbsp; `
            + `<span style="color:${C.dict}">■</span> Dictionary ${fmt(it.dict)} &nbsp; `
            + `<span style="color:${C.hash}">■</span> Hash index ${fmt(it.hash)}${rem}`;
        },
      },
      xAxis: { type: 'value', axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => fmt(v) }, splitLine: { lineStyle: { color: border } } },
      yAxis: {
        type: 'category', inverse: true, data: rows.map((r) => r.label),
        axisLabel: { color: fg, fontSize: 11, width: 150, overflow: 'truncate' },
        axisLine: { lineStyle: { color: border } }, axisTick: { show: false },
      },
      series,
    };
  }, [rows, storageMode, muted, fg, border, C.data, C.dict, C.hash]);

  const onEvents = useMemo(() => ({
    click: (p: any) => { const it = rows[p?.dataIndex]; if (it) onSelect?.(it.ref); },
  }), [rows, onSelect]);

  return <EChart option={option} height={Math.max(140, rows.length * 30 + 28)} onEvents={onEvents} />;
}

// ---- DAX benchmark bars ---------------------------------------------------------------------
export function BenchBars({ runsMs }: { runsMs: number[] }) {
  const accent = cssVar('--sem-accent', '#2ED47A');
  const muted = cssVar('--sem-muted', '#9aa0aa');
  const border = cssVar('--sem-border', 'rgba(140,140,160,0.22)');
  const option = useMemo<echarts.EChartsCoreOption>(() => ({
    grid: { left: 38, right: 10, top: 10, bottom: 22 },
    xAxis: { type: 'category', data: runsMs.map((_, i) => `#${i + 1}`), axisLine: { lineStyle: { color: border } }, axisLabel: { color: muted, fontSize: 10 } },
    yAxis: { type: 'value', name: 'ms', nameTextStyle: { color: muted, fontSize: 10 }, axisLabel: { color: muted, fontSize: 10 }, splitLine: { lineStyle: { color: border } } },
    tooltip: { trigger: 'axis', formatter: (p: any) => `${p[0].value} ms` },
    series: [{ type: 'bar', data: runsMs, itemStyle: { color: accent, borderRadius: [3, 3, 0, 0] } }],
  }), [runsMs, accent, muted, border]);
  return <EChart option={option} height={140} />;
}

// ---- Readiness trend sparkline --------------------------------------------------------------
export function Sparkline({ values, height = 40 }: { values: number[]; height?: number }) {
  const accent = cssVar('--sem-accent', '#2ED47A');
  const option = useMemo<echarts.EChartsCoreOption>(() => ({
    grid: { left: 2, right: 2, top: 4, bottom: 2 },
    xAxis: { type: 'category', show: false, data: values.map((_, i) => i) },
    yAxis: { type: 'value', show: false, min: 0, max: 100 },
    tooltip: { trigger: 'axis', formatter: (p: any) => `${Number(p[0].value).toFixed(1)}/100` },
    series: [{
      type: 'line', data: values, smooth: true, symbol: 'none',
      lineStyle: { color: accent, width: 2 },
      areaStyle: { color: 'rgba(46,212,122,0.20)' }, // Signal-green wash at --sem-accent-soft's dark-theme weight (20%); was the pre-rebrand purple
    }],
  }), [values, accent]);
  return <EChart option={option} height={height} />;
}
