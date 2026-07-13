import { useEffect, useMemo, useRef, useState } from 'react';
import type * as echarts from 'echarts';
import { EChart } from './echart';
import { KIND_COLOR, KIND_GLYPH, KIND_LABEL, fieldFacetOptions, makeScope, rankNodeMatches, resolveColor, tableFacetOptions, useFillHeight, type LineageResult } from './lineagetypes';
import { MultiSelect } from './lineageslicer';

// ===================================================================================================
// View A — the Obsidian-style force-directed lineage graph. Reuses the EChart wrapper (ECharts `graph`
// series). Nodes are grouped+coloured by kind (categories → legend → one-click type filter). Clicking a node
// SELECTS it (lights it + its direct neighbours, fades the rest, shows the detail card); clicking another node
// walks the selection there; clicking empty space deselects; Reset restores the whole default view. On top of
// that, three readability controls (Increment 2): a SEARCH-to-focus box, a "Focused" scope that shows only
// the depth-N neighbourhood around the picked node (a BFS over the edges — so a 1000s-node model is never a
// hairball), a DEPTH slider, and kind FILTER chips that subset the graph.
// ===================================================================================================

function esc(s: string): string {
  return (s ?? '').replace(/[&<>"]/g, (c) => (c === '&' ? '&amp;' : c === '<' ? '&lt;' : c === '>' ? '&gt;' : '&quot;'));
}

// Edge semantics → colour (the kinds get_lineage emits on each edge).
const EDGE_COLOR: Record<string, string> = {
  dependsOn: '#2ED47A', relationship: '#e0b341', contains: 'rgba(150,150,170,0.45)', source: '#4E7CA8', reportUsage: '#ff8c42',
};
const EDGE_LABEL: Record<string, string> = {
  dependsOn: 'depends on', relationship: 'relationship', contains: 'contains', source: 'source', reportUsage: 'shown in',
};

type Layout = 'force' | 'circular';
type Scope = 'all' | 'focus';

// Defaults for the user-facing graph controls — declared once so the useState initials and resetView() can never
// drift apart (Reset must restore EVERY control on the toolbar, not just selection/filters).
const DEFAULT_LAYOUT: Layout = 'force';
const DEFAULT_DEPTH = 2;
const DEFAULT_SIZE = 0.85;

// Undirected BFS neighbourhood of `root` to `depth` hops over `edges` — the local-view subgraph.
function neighborhood(root: string, edges: { from: string; to: string }[], depth: number): Set<string> {
  const adj = new Map<string, string[]>();
  const push = (a: string, b: string) => { const l = adj.get(a); if (l) l.push(b); else adj.set(a, [b]); };
  for (const e of edges) { push(e.from, e.to); push(e.to, e.from); }
  const seen = new Set<string>([root]);
  let frontier = [root];
  for (let d = 0; d < depth; d++) {
    const next: string[] = [];
    for (const u of frontier) for (const v of adj.get(u) ?? []) if (!seen.has(v)) { seen.add(v); next.push(v); }
    frontier = next;
    if (frontier.length === 0) break;
  }
  return seen;
}

export function LineageGraphView({ graph, onOpenImpact }: { graph: LineageResult | null; onOpenImpact: (ref: string) => void }) {
  const [layout, setLayout] = useState<Layout>(DEFAULT_LAYOUT);
  const [selected, setSelected] = useState<{ ref: string; name: string; kind: string; table?: string } | null>(null);
  const [focusRef, setFocusRef] = useState<string | null>(null);   // the node the local view centres on
  const [scope, setScope] = useState<Scope>('all');
  const [depth, setDepth] = useState(DEFAULT_DEPTH);
  const [disabled, setDisabled] = useState<Set<string>>(new Set());   // kinds hidden via the filter chips (empty = all shown)
  const [selTables, setSelTables] = useState<Set<string>>(new Set());   // slicer: scope to a subset of tables (empty = all)
  const [selFields, setSelFields] = useState<Set<string>>(new Set());   // slicer: scope to specific measures/columns (empty = all)
  const [search, setSearch] = useState('');
  // WALK model: ONE selected node at a time. Selecting a node lights it + its direct neighbours and shows the detail
  // card; clicking ANY node (lit neighbour or faded stranger) MOVES the selection there, so repeated clicks walk the
  // graph one hop at a time with the highlight following. Click empty canvas or Clear selection to deselect.
  // Everything outside the lit set fades, and hover-emphasis is disabled while a selection is active.
  const [resetNonce, setResetNonce] = useState(0);   // Reset bumps this to force a fresh layout even from default state
  const [sizeScale, setSizeScale] = useState(DEFAULT_SIZE);   // node-size slider (multiplies the degree-based bubble size)
  const chartRef = useRef<echarts.ECharts | null>(null);
  const autoFocused = useRef(false);   // one-shot: large models open in Focused so the first view isn't a hairball
  // Throttle the size slider to one update per animation frame: a raw drag fires dozens of events/sec, each triggering
  // an O(N) style-merge + canvas repaint. Coalescing to ~60fps keeps dragging smooth on a big graph.
  const sizeRaf = useRef<number | null>(null);
  const pendingSize = useRef<number | null>(null);
  const onSizeChange = (v: number) => {
    pendingSize.current = v;
    if (sizeRaf.current == null) sizeRaf.current = requestAnimationFrame(() => { sizeRaf.current = null; if (pendingSize.current != null) setSizeScale(pendingSize.current); });
  };

  const fg = resolveColor('var(--sem-fg)', '#ddd');
  const muted = resolveColor('var(--sem-muted)', '#9aa0aa');
  const accent = resolveColor('var(--sem-accent)', '#2ED47A');
  const surface = resolveColor('var(--sem-surface)', '#1e1e2a');
  const border = resolveColor('var(--sem-border)', 'rgba(140,140,160,0.22)');

  const kindsPresent = useMemo(() => [...new Set((graph?.nodes ?? []).map((n) => n.kind))], [graph]);
  const edgeKindsPresent = useMemo(() => [...new Set((graph?.edges ?? []).map((e) => e.kind))], [graph]);
  // Slicer options are built from the WHOLE model (not the filtered view) so the dropdowns always list everything.
  const tableOptions = useMemo(() => tableFacetOptions(graph?.nodes ?? []), [graph]);
  const fieldOptions = useMemo(() => fieldFacetOptions(graph?.nodes ?? []), [graph]);
  const disabledKey = [...disabled].sort().join(',');
  const tablesKey = [...selTables].sort().join(',');
  const fieldsKey = [...selFields].sort().join(',');
  // Fill the viewport height (remeasure when the control rows above can change height: scope toggles the depth slider,
  // the edge-key row appears once edges load).
  const [fillRef, chartH] = useFillHeight(440, [scope, edgeKindsPresent.length]);

  // Topology signature so a non-structural live refresh reuses the same option (no re-layout / no focus wipe).
  const sig = useMemo(() => {
    const ns = graph?.nodes ?? [], es = graph?.edges ?? [];
    return ns.length + '/' + es.length + '|' + ns.map((n) => n.ref + (n.isHidden ? '*' : '')).join(',')
      + '|' + es.map((e) => e.from + '>' + e.to + ':' + e.kind).join(',');
  }, [graph]);

  const { option, shown, total } = useMemo(() => {
    const allNodes = graph?.nodes ?? [];
    const allEdges = graph?.edges ?? [];

    // 1) kind filter + slicer scope (tables/fields) — both subset the visible nodes; edges among survivors are kept below
    const scopeSel = makeScope(selTables, selFields);
    let nodes = allNodes.filter((n) => !disabled.has(n.kind) && scopeSel.has(n));
    let ids = new Set(nodes.map((n) => n.ref));
    const kindByRef = new Map(allNodes.map((n) => [n.ref, n.kind]));
    // Drop the table→measure 'contains' edges (perf + declutter): they're the biggest edge group (one per measure) and
    // are structural "home table" links, not data-flow — the tree already ignores them in buildDeps. Measures stay
    // connected via their own dependsOn edges, so nothing is orphaned; the whole-model view sheds ~1 edge per measure.
    let edges = allEdges.filter((e) => ids.has(e.from) && ids.has(e.to) && !(e.kind === 'contains' && kindByRef.get(e.to) === 'measure'));

    // 2) focus scope — keep only the depth-N neighbourhood of the focused node
    if (scope === 'focus' && focusRef && ids.has(focusRef)) {
      const keep = neighborhood(focusRef, edges, depth);
      nodes = nodes.filter((n) => keep.has(n.ref));
      ids = new Set(nodes.map((n) => n.ref));
      edges = edges.filter((e) => ids.has(e.from) && ids.has(e.to));
    }

    const degree = new Map<string, number>();
    const links = edges.map((e) => {
      degree.set(e.from, (degree.get(e.from) ?? 0) + 1);
      degree.set(e.to, (degree.get(e.to) ?? 0) + 1);
      return { source: e.from, target: e.to, lineStyle: { color: EDGE_COLOR[e.kind] ?? muted, width: 1, opacity: 0.55, curveness: 0 }, _kind: e.kind };
    });

    const kinds = [...new Set(nodes.map((n) => n.kind))];
    const catIndex = new Map(kinds.map((k, i) => [k, i]));
    const categories = kinds.map((k) => ({ name: KIND_LABEL[k] ?? k, itemStyle: { color: resolveColor(KIND_COLOR[k], muted) } }));

    const data = nodes.map((n) => {
      const deg = degree.get(n.ref) ?? 0;
      const isFocus = n.ref === focusRef;
      return {
        id: n.ref, name: n.name, category: catIndex.get(n.kind),
        symbolSize: Math.max(7, Math.min(7 + deg * 2, 34)) * (isFocus ? 1.25 : 1),
        itemStyle: {
          color: resolveColor(KIND_COLOR[n.kind], muted), opacity: n.isHidden ? 0.5 : 1,
          borderColor: isFocus ? accent : 'transparent', borderWidth: isFocus ? 2.5 : 0,
        },
        _kind: n.kind, _table: n.table ?? '', _deg: deg, _hidden: !!n.isHidden,
      };
    });

    const showLabels = nodes.length <= 70;
    const opt: echarts.EChartsCoreOption = {
      backgroundColor: 'transparent',
      // No ECharts legend — the kind-filter chips in the toolbar are the colour key AND the (hard) type filter.
      tooltip: {
        confine: true, backgroundColor: surface, borderColor: border, borderWidth: 1, textStyle: { color: fg, fontSize: 12 },
        formatter: (p: any) => {
          if (p.dataType === 'edge') return `<span style="color:${muted}">${esc(p.data._kind ?? 'depends on')}</span>`;
          const d = p.data;
          return `<b>${esc(d.name)}</b><br/><span style="color:${muted}">${esc(KIND_LABEL[d._kind] ?? d._kind)}${d._table ? ' · ' + esc(d._table) : ''}</span>`
            + `<br/><span style="color:${muted}">${d._deg} connection${d._deg === 1 ? '' : 's'}${d._hidden ? ' · hidden' : ''} · click to select</span>`;
        },
      },
      series: [{
        type: 'graph', layout, roam: true, draggable: true, scaleLimit: { min: 0.3, max: 6 },
        categories, data, links,
        edgeSymbol: ['none', 'arrow'], edgeSymbolSize: [0, 7],
        // layoutAnimation:false → the force settles SYNCHRONOUSLY in one pass (no per-frame animation loop). This (a) is
        // far cheaper on a big graph (no continuous re-simulation/repaint), and (b) guarantees getItemLayout returns
        // SETTLED positions immediately, so the position-freeze below can never capture an unsettled initial scatter.
        force: { repulsion: 240, edgeLength: [60, 170], gravity: 0.05, friction: 0.6, layoutAnimation: false },
        circular: { rotateLabel: true },
        label: { show: showLabels, position: 'right', color: fg, fontSize: 10, formatter: (p: any) => p.data?.name ?? '' },
        labelLayout: { hideOverlap: true },
        lineStyle: { color: muted, width: 1, opacity: 0.5 },
        emphasis: { focus: 'adjacency', blurScope: 'coordinateSystem', scale: 1.08, label: { show: true }, itemStyle: { shadowBlur: 6, shadowColor: accent }, lineStyle: { width: 2, opacity: 0.95 } },
        blur: { itemStyle: { opacity: 0.07 }, lineStyle: { opacity: 0.04 }, label: { show: false } },
      }],
    };
    return { option: opt, shown: nodes.length, total: allNodes.length };
    // `graph` is read in the body but intentionally NOT a dep — `sig` captures the render-relevant structure.
    // `resetNonce` is a dep so Reset emits a NEW option object even when every control is already at its default:
    // the EChart wrapper applies options with notMerge, so that one bump discards the frozen node positions (baked
    // in by the highlight effect below) and any roam zoom/pan, and re-runs the force layout from scratch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig, layout, scope, focusRef, depth, disabledKey, tablesKey, fieldsKey, resetNonce, fg, muted, accent, surface, border]);

  // Refs so the (mount-bound) chart-event handlers read live state without rebinding.
  const optionRef = useRef(option); optionRef.current = option;

  // Undirected adjacency of what's currently shown (from the option's links) — used to expand the selection to its
  // immediate neighbours so selecting lights the node AND its connections.
  const adjacency = useMemo(() => {
    const adj = new Map<string, Set<string>>();
    const link = (a: string, b: string) => { let s = adj.get(a); if (!s) { s = new Set(); adj.set(a, s); } s.add(b); };
    for (const l of ((option as any)?.series?.[0]?.links ?? []) as { source: string; target: string }[]) { link(l.source, l.target); link(l.target, l.source); }
    return adj;
  }, [option]);
  // The lit set = the selected node plus ALL its direct neighbours — the whole point of selecting is reading what it
  // connects to, so nothing adjacent is culled. Everything else fades.
  const lit = useMemo<Set<string> | null>(() => {
    if (!selected) return null;
    const nodes = new Set<string>([selected.ref]);
    for (const nb of adjacency.get(selected.ref) ?? []) nodes.add(nb);
    return nodes;
  }, [selected, adjacency]);

  // Apply the walk highlight + node size WITHOUT a relayout. Baking each node's current position (x/y + `fixed`) into the
  // data-merge stops ECharts' force layout from re-running (no "re-orient on click"); symbolSize is scaled here too so
  // the size slider resizes in place. An edge lights only when BOTH ends are lit. Runs after the base option setOption.
  useEffect(() => {
    const c = chartRef.current; if (!c || (c as any).isDisposed?.()) return;
    const s0 = (optionRef.current as any)?.series?.[0];
    const data = s0?.data as any[] | undefined, links = s0?.links as any[] | undefined;
    if (!data || !links) return;
    const layById = new Map<string, number[]>();
    try {
      const sd = (c as any).getModel().getSeriesByIndex(0)?.getData();
      if (sd) for (let i = 0; i < sd.count(); i++) { const l = sd.getItemLayout(i); if (l) layById.set(sd.getId(i), l); }
    } catch { /* positions optional */ }
    const on = lit != null;
    // The whole point of selecting a node is to READ the names of what it connects to — so when a selection is
    // active, label the ENTIRE lit set (the node AND its neighbours). labelLayout.hideOverlap (base option) culls
    // only the labels that physically collide, so even a big ring stays legible instead of a wall of text. When
    // nothing is selected, keep base behaviour (labels on a small graph only, so a hairball isn't drowned in text).
    const showLabels = data.length <= 70;
    const nd = data.map((d) => {
      const bo = d._hidden ? 0.5 : 1;
      const isLit = !on || lit!.has(d.id);
      const isSel = on && d.id === selected?.ref;
      const lp = layById.get(d.id);
      return { ...d,
        ...(lp ? { x: lp[0], y: lp[1], fixed: true } : {}),
        symbolSize: (d.symbolSize ?? 8) * sizeScale,
        itemStyle: { ...d.itemStyle, opacity: isLit ? bo : 0.05, borderColor: isSel ? accent : (d.itemStyle?.borderColor ?? 'transparent'), borderWidth: isSel ? 2.5 : (d.itemStyle?.borderWidth ?? 0) },
        label: { show: on ? isLit : showLabels } };
    });
    const nl = links.map((l) => {
      const isLit = !on || (lit!.has(l.source) && lit!.has(l.target));
      return { ...l, lineStyle: { ...l.lineStyle, opacity: isLit ? (l.lineStyle?.opacity ?? 0.55) : 0.03 } };
    });
    // emphasis.disabled while a selection is active so hovering the faded background can't re-light it.
    c.setOption({ series: [{ emphasis: { disabled: on }, data: nd, links: nl }] });
  }, [lit, selected, option, accent, sizeScale]);

  // Large models open in FOCUSED (a local view) instead of dumping a whole-model hairball — one-shot, on first load,
  // seeded at the highest-degree node so there's an immediate meaningful view. The user can switch to Whole model anytime.
  useEffect(() => {
    if (autoFocused.current || !graph || (graph.nodes?.length ?? 0) <= 150) return;
    autoFocused.current = true;
    const deg = new Map<string, number>();
    for (const e of graph.edges) { deg.set(e.from, (deg.get(e.from) ?? 0) + 1); deg.set(e.to, (deg.get(e.to) ?? 0) + 1); }
    // Prefer the highest-degree MEASURE (a meaningful, tight starting point) over the raw highest-degree node — in a
    // real model that's usually a Date column or a table, which is connected to nearly everything (a huge, unhelpful focus).
    let best: string | null = null, bestDeg = -1, bestMeasure: string | null = null, bestMeasureDeg = -1;
    for (const n of graph.nodes) {
      const d = deg.get(n.ref) ?? 0;
      if (d > bestDeg) { bestDeg = d; best = n.ref; }
      if (n.kind === 'measure' && d > bestMeasureDeg) { bestMeasureDeg = d; bestMeasure = n.ref; }
    }
    const pick = bestMeasure ?? best;
    if (pick) { setFocusRef(pick); setScope('focus'); }
  }, [graph]);

  // Fit ALL shown nodes into the viewport. ECharts `graph` has no built-in fit (force layout can spread far wider than
  // the canvas → nodes render off-screen, unreachable without manual scroll/zoom). Measure the node pixel bounds after
  // the layout settles, then roam-zoom+pan to frame them with padding. `graphRoam` is relative, so it composes with any
  // current pan/zoom; zooming about the bbox centre keeps that screen point fixed, then a pan recentres it.
  const fitToView = () => {
    const c = chartRef.current; if (!c || (c as any).isDisposed?.()) return;
    let data: any;
    try { data = (c as any).getModel().getSeriesByIndex(0)?.getData(); } catch { return; }
    const n = data?.count?.() ?? 0; if (!n) return;
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity, seen = 0;
    for (let i = 0; i < n; i++) {
      const lay = data.getItemLayout(i); if (!lay) continue;
      let p; try { p = c.convertToPixel({ seriesIndex: 0 }, lay); } catch { p = lay; }
      if (!p || !isFinite(p[0]) || !isFinite(p[1])) continue;
      seen++;
      if (p[0] < minX) minX = p[0]; if (p[0] > maxX) maxX = p[0];
      if (p[1] < minY) minY = p[1]; if (p[1] > maxY) maxY = p[1];
    }
    if (seen === 0 || !isFinite(minX)) return;
    const W = c.getWidth(), H = c.getHeight();
    const pw = Math.max(1, maxX - minX), ph = Math.max(1, maxY - minY);
    const zoom = Math.max(0.15, Math.min((W * 0.86) / pw, (H * 0.86) / ph, 4));
    const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
    c.dispatchAction({ type: 'graphRoam', zoom, originX: cx, originY: cy });   // zoom keeps (cx,cy) fixed on screen…
    c.dispatchAction({ type: 'graphRoam', dx: W / 2 - cx, dy: H / 2 - cy });   // …then recentre it
  };

  // Auto-fit once per STRUCTURAL view (new topology / scope / focus / depth / filter / layout / reset) — NOT on a
  // select/hover (those don't change fitKey), so a user's manual zoom is preserved while they walk the graph.
  const fitKey = sig + '|' + layout + '|' + scope + '|' + (focusRef ?? '') + '|' + depth + '|' + disabledKey + '|' + tablesKey + '|' + fieldsKey + '|' + resetNonce;
  useEffect(() => {
    const c = chartRef.current; if (!c || (c as any).isDisposed?.()) return;
    let done = false;
    const run = () => { if (done) return; done = true; fitToView(); };
    c.on('finished', run);                       // fires after layout+render settle
    const t = window.setTimeout(run, 1400);      // fallback if 'finished' already fired
    return () => { c.off('finished', run); window.clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fitKey]);

  // Node clicks are handled by a SINGLE forgiving snap-to-nearest handler bound on the ZRender canvas (see onChartReady),
  // not ECharts' exact-hit onEvents — so a click that grazes a tiny node still lands. A click only moves the SELECTION;
  // it never re-centres/relayouts (Kane: "re-orient is distracting").

  // Hover blur (focus:'adjacency') is ECharts STATE, separate from our option-level fade. It normally exits on the
  // next real mouse move, but a pointer that hasn't moved since the hover (or synthetic events) can leave it stuck
  // over a freshly cleared view — so exit it explicitly whenever the selection clears.
  const downplayHover = () => { try { chartRef.current?.dispatchAction({ type: 'downplay', seriesIndex: 0 }); } catch { /* chart gone */ } };

  const clearSelection = () => { setSelected(null); downplayHover(); };

  // Reset = the full graph, nothing selected, no filters, default layout/size/depth, default zoom/pan, layout settled
  // fresh. All React state the control row binds goes back to defaults (the DEFAULT_* constants keep this list and the
  // useState initials in lockstep), and resetNonce forces a fresh option/layout (see the option memo) which fitKey
  // then frames.
  const resetView = () => {
    setSelected(null); setFocusRef(null); setScope('all');
    setDisabled(new Set()); setSelTables(new Set()); setSelFields(new Set()); setSearch('');
    setLayout(DEFAULT_LAYOUT); setDepth(DEFAULT_DEPTH);
    pendingSize.current = null;   // an in-flight slider rAF frame must not overwrite the reset a beat later
    setSizeScale(DEFAULT_SIZE);
    setResetNonce((n) => n + 1);
    downplayHover();
  };

  // A live model refresh can delete or rename the selected/focused node. Prune both so a dead selection can never
  // leave the whole graph faded with nothing lit (and hover disabled), and a dead focus can never strand an empty view.
  useEffect(() => {
    const ids = new Set((graph?.nodes ?? []).map((n) => n.ref));
    setSelected((s) => (s && !ids.has(s.ref) ? null : s));
    if (focusRef && !ids.has(focusRef)) { setFocusRef(null); setScope('all'); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig]);

  // search suggestions — ranked (exact > prefix > substring; measures/tables before columns before sources)
  const matches = useMemo(() => rankNodeMatches(graph?.nodes ?? [], search), [search, graph]);

  const focusOn = (ref: string, name: string, kind: string, table?: string) => {
    setSelected({ ref, name, kind, table });   // searching to a node selects it (lights it + its connections)
    setFocusRef(ref);
    setScope('focus');
    setSearch('');
  };
  const toggleKind = (k: string) => setDisabled((s) => { const n = new Set(s); n.has(k) ? n.delete(k) : n.add(k); return n; });

  if (!graph) return <Panel><Empty>Loading the lineage graph…</Empty></Panel>;
  if (total === 0) return <Panel><Empty>No objects to graph yet. Open a model.</Empty></Panel>;

  return (
    <Panel className="p-0 overflow-hidden">
      {/* toolbar */}
      <div className="flex items-center gap-2 px-3 py-2 border-b" style={{ borderColor: 'var(--sem-border)' }}>
        <span className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Dependency graph</span>
        <span className="text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>{shown < total ? `${shown} of ${total}` : `${total}`} nodes</span>
        <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>· click a node to see what it connects to · click another to walk there · click empty space to clear · scroll/drag to navigate</span>
        {selected && <span className="text-[10px] tnum px-1.5 py-0.5 rounded truncate" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', maxWidth: 180 }}>selected: {selected.name}</span>}
        <div className="ml-auto flex items-center gap-1.5">
          {selected && <ToolBtn onClick={clearSelection}>Clear selection</ToolBtn>}
          <label className="flex items-center gap-1 text-[10px]" style={{ color: 'var(--sem-muted)' }} title="Node size">
            <span style={{ fontSize: 9 }}>size</span>
            <input type="range" min={0.4} max={2} step={0.05} value={sizeScale} onChange={(e) => onSizeChange(Number(e.target.value))} style={{ width: 64, accentColor: 'var(--sem-accent)' }} />
          </label>
          <ToolBtn active={layout === 'force'} onClick={() => setLayout('force')}>Force</ToolBtn>
          <ToolBtn active={layout === 'circular'} onClick={() => setLayout('circular')}>Circular</ToolBtn>
          <ToolBtn onClick={fitToView}>Fit</ToolBtn>
          <ToolBtn onClick={resetView}>Reset</ToolBtn>
        </div>
      </div>

      {/* controls: search · scope/depth · kind filters */}
      <div className="flex items-center gap-3 px-3 py-2 border-b flex-wrap" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
        <div className="relative">
          <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search a node to focus…" spellCheck={false}
            onKeyDown={(e) => { if (e.key === 'Enter' && matches[0]) focusOn(matches[0].ref, matches[0].name, matches[0].kind, matches[0].table); else if (e.key === 'Escape') setSearch(''); }}
            className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ width: 220, background: 'var(--sem-bg)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          {matches.length > 0 && (
            <div className="absolute z-10 mt-1 rounded-md overflow-hidden" style={{ width: 260, background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', boxShadow: '0 6px 20px rgba(0,0,0,0.4)' }}>
              {matches.map((m) => (
                <button key={m.ref} onMouseDown={(e) => { e.preventDefault(); focusOn(m.ref, m.name, m.kind, m.table); }}
                  className="flex items-center gap-2 w-full text-left px-2 py-1 text-[12px] hover:bg-[var(--sem-surface-2)]">
                  <span style={{ color: resolveColor(KIND_COLOR[m.kind], '#9aa0aa') }}>{KIND_GLYPH[m.kind] ?? '•'}</span>
                  <span className="truncate flex-1">{m.name}</span>
                  {m.table && <span className="text-[10px] truncate" style={{ color: 'var(--sem-muted)', maxWidth: 90 }}>{m.table}</span>}
                </button>
              ))}
            </div>
          )}
        </div>

        {/* Slicers — scope the whole picture to a subset of tables / specific fields (multi-select; the search box only
            picks one node). Union: pick tables AND/OR specific measures & columns. A count badge shows when active. */}
        <div className="flex items-center gap-1.5">
          <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>slice</span>
          <MultiSelect label="Tables" options={tableOptions} selected={selTables} onChange={setSelTables} width={220} />
          <MultiSelect label="Fields" options={fieldOptions} selected={selFields} onChange={setSelFields} width={260} />
          {(selTables.size > 0 || selFields.size > 0) && <ToolBtn onClick={() => { setSelTables(new Set()); setSelFields(new Set()); }}>Clear slice</ToolBtn>}
        </div>

        <div className="flex items-center gap-1.5">
          <ToolBtn active={scope === 'all'} onClick={() => setScope('all')}>Whole model</ToolBtn>
          <ToolBtn active={scope === 'focus'} onClick={() => { if (selected && !focusRef) setFocusRef(selected.ref); setScope('focus'); }}>Focused</ToolBtn>
          {scope === 'focus' && (
            <label className="flex items-center gap-1.5 text-[11px] ml-1" style={{ color: 'var(--sem-muted)' }}>
              depth
              <input type="range" min={1} max={4} value={depth} onChange={(e) => setDepth(Number(e.target.value))} style={{ width: 80, accentColor: 'var(--sem-accent)' }} />
              <span className="tnum w-3">{depth}</span>
            </label>
          )}
          {scope === 'focus' && !focusRef && <span className="text-[10px]" style={{ color: 'var(--sem-warn)' }}>click or search a node</span>}
        </div>

        <div className="flex items-center gap-1 flex-wrap ml-auto">
          {kindsPresent.map((k) => {
            const on = !disabled.has(k);
            return (
              <button key={k} onClick={() => toggleKind(k)} title={(on ? 'Hide ' : 'Show ') + (KIND_LABEL[k] ?? k)}
                className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded-md"
                style={{ background: on ? 'var(--sem-bg)' : 'transparent', border: '1px solid var(--sem-border)', color: on ? 'var(--sem-fg)' : 'var(--sem-muted)', opacity: on ? 1 : 0.5 }}>
                <span style={{ color: resolveColor(KIND_COLOR[k], '#9aa0aa') }}>{KIND_GLYPH[k] ?? '•'}</span>
                {KIND_LABEL[k] ?? k}
              </button>
            );
          })}
        </div>
      </div>

      {/* edge-kind colour key — explains the directed edge colours (node kinds are the chips above) */}
      {edgeKindsPresent.length > 0 && (
        <div className="flex items-center gap-3 px-3 py-1 border-b flex-wrap" style={{ borderColor: 'var(--sem-border)' }}>
          <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>edges</span>
          {edgeKindsPresent.map((k) => (
            <span key={k} className="flex items-center gap-1 text-[10px]" style={{ color: 'var(--sem-muted)' }}>
              <span style={{ width: 14, height: 0, borderTop: '2px solid ' + (EDGE_COLOR[k] ?? muted), display: 'inline-block' }} />
              {EDGE_LABEL[k] ?? k}
            </span>
          ))}
          <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>· arrow points from a dependant to what it depends on</span>
        </div>
      )}

      {/* chart */}
      <div ref={fillRef} style={{ position: 'relative' }}>
        <EChart option={option} height={chartH}
          onChartReady={(c) => {
            chartRef.current = c;
            // CLICK vs DRAG: ZRender proxies the browser's NATIVE click, which fires after ANY mousedown+mouseup on
            // the canvas — including a node drag and a roam pan. Without disambiguation, every drag/pan release used
            // to register as a click and select whatever node was near the release point (the "buggy on clicks").
            // Track the press point and only treat the release as a click if the pointer barely moved.
            let downPt: { x: number; y: number } | null = null;
            c.getZr().on('mousedown', (e: any) => { downPt = { x: e.offsetX, y: e.offsetY }; });
            // FORGIVING CLICK: snap the click to the NEAREST node within ~30px and select it. Clicking a tiny node
            // in a dense graph exactly is hard — a click that grazes an edge or the gap would otherwise register
            // nothing, which reads as "walk doesn't work". A genuine empty-space click DESELECTS (safe now that
            // drag/pan releases are filtered out above).
            c.getZr().on('click', (e: any) => {
              const moved = downPt ? Math.hypot(e.offsetX - downPt.x, e.offsetY - downPt.y) : 0;
              downPt = null;
              if (moved > 4) return;   // it was a drag or a pan, not a click
              const inst = chartRef.current; if (!inst || (inst as any).isDisposed?.()) return;
              let data: any; try { data = (inst as any).getModel().getSeriesByIndex(0)?.getData(); } catch { return; }
              if (!data || !data.count) return;
              const px = e.offsetX, py = e.offsetY; let best = -1, bestD = Infinity;
              for (let i = 0; i < data.count(); i++) {
                const lay = data.getItemLayout(i); if (!lay) continue;
                let pt: any; try { pt = inst.convertToPixel({ seriesIndex: 0 }, lay); } catch { pt = lay; }
                if (!pt) continue; const dx = pt[0] - px, dy = pt[1] - py, dsq = dx * dx + dy * dy;
                if (dsq < bestD) { bestD = dsq; best = i; }
              }
              // Empty space → deselect (and exit any stale hover blur, so the whole graph really comes back).
              if (best < 0 || bestD > 30 * 30) {
                setSelected(null);
                try { inst.dispatchAction({ type: 'downplay', seriesIndex: 0 }); } catch { /* chart gone */ }
                return;
              }
              const id = data.getId(best);
              const nd = ((optionRef.current as any)?.series?.[0]?.data || []).find((d: any) => d.id === id);
              // Selection MOVES (never accumulates): clicking a lit neighbour walks the highlight one hop over.
              setSelected({ ref: id, name: nd?.name ?? id, kind: nd?._kind ?? 'unresolved', table: nd?._table });
            });
          }} />
        {shown === 0 && (
          <div className="absolute inset-0 flex items-center justify-center text-[12px]" style={{ color: 'var(--sem-muted)' }}>
            {scope === 'focus' && !focusRef ? 'Click or search a node to focus a local view.' : 'No nodes match the current filters.'}
          </div>
        )}
        {selected && (
          <div className="absolute left-3 bottom-3 flex items-center gap-2 px-3 py-2 rounded-lg text-[12px]"
            style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', backdropFilter: 'blur(4px)' }}>
            <span className="shrink-0" style={{ color: resolveColor(KIND_COLOR[selected.kind], '#9aa0aa') }}>{KIND_GLYPH[selected.kind] ?? '•'}</span>
            <span className="font-medium truncate" style={{ maxWidth: 200 }}>{selected.name}</span>
            {selected.table && <span style={{ color: 'var(--sem-muted)' }} className="truncate">· {selected.table}</span>}
            {scope === 'all' && <button onClick={() => { setFocusRef(selected.ref); setScope('focus'); }} className="ml-1 text-[11px] px-2 py-0.5 rounded font-medium" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>Focus local</button>}
            <button onClick={() => onOpenImpact(selected.ref)} className="text-[11px] px-2 py-0.5 rounded font-medium" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Assess change</button>
          </div>
        )}
      </div>
    </Panel>
  );
}

// ---- local primitives (kept consistent with lineage.tsx) -------------------------------------
function Panel({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`rounded-xl border ${className.includes('p-0') ? '' : 'p-4'} ${className}`} style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function Empty({ children }: { children: React.ReactNode }) {
  return <div className="text-[12px] py-10 text-center" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}
function ToolBtn({ active, onClick, children }: { active?: boolean; onClick: () => void; children: React.ReactNode }) {
  // active:scale + brightness gives INSTANT tactile feedback on press (no wait for the re-render), so a click never
  // "feels like nothing happened" even when the result (relayout/refetch) takes a beat.
  return (
    <button onClick={onClick} className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-[transform,filter,background-color] duration-100 active:scale-90 active:brightness-125 hover:brightness-110"
      style={active ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
