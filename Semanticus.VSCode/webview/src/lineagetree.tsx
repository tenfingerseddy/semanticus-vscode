import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ReactFlow, ReactFlowProvider, Background, Controls, MiniMap, Handle, Position,
  useNodesState, useEdgesState, useReactFlow, type Node, type Edge, type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import dagre from '@dagrejs/dagre';
import { KIND_COLOR, KIND_GLYPH, KIND_LABEL, fieldFacetOptions, makeScope, rankNodeMatches, resolveColor, tableFacetOptions, useFillHeight, type LineageNode, type LineageResult } from './lineagetypes';
import { MultiSelect } from './lineageslicer';
import { CANVAS_CHIP_MIN_H, CanvasChip, RowMenu, RowSep, ToolRow, canvasInsetStyle, useToolRow, visibleBottomInset } from './toolrow';
import { uiLabel } from './copy';

// ===================================================================================================
// View B — the DAG / tree lineage view (React Flow + dagre). Pick a root, then EXPAND-ON-CLICK to reveal
// the next ring of its dependencies (Upstream — what it's built from) or its dependants (Downstream — the
// impact / "measure killer"). React Flow handles the MULTI-PARENT DAG (a conformed dimension or base measure
// has many parents) that an ECharts `tree` could not. dagre lays out left→right ranks; the focus node is on
// the left, revealed neighbours flow right. A node carries its safe-to-remove verdict (joined from
// unused_objects) so you can see what's safe as you trace.
// ===================================================================================================

const NODE_W = 188;
const NODE_H = 52;
// React Flow's minimap is a FIXED box (200x150 plus its 1px border) however small the pane gets — its size never
// tracks the canvas, which is why a short pane turns it from a corner overview into a curtain over the cards. Measured
// in the built bundle: 202x152. Used only to decide whether it still fits as a corner (see showMiniMap).
// (Wording note: Tailwind scans the RAW source for class candidates, comments included, so an ordinary English word
// that happens to be a utility name emits a stray rule into the shipped CSS. This paragraph used one and grew the
// bundle by a rule that nothing renders. Prefer a synonym in prose whenever a word doubles as a Tailwind class.)
const MINIMAP_BOX = 202 * 152;

type Dir = 'upstream' | 'downstream';
interface UnusedLite { ref: string; verdict: string }

// Canonical dependency adjacency (dependant → dependency), normalising each edge kind's stored direction
// (LineageProtocol: dependsOn = dependant→dependency; relationship = many→one; source = source→table;
// contains = table→child). So Upstream = follow dependency links forward, Downstream = follow them backward.
function buildDeps(graph: LineageResult | null) {
  const kind = new Map((graph?.nodes ?? []).map((n) => [n.ref, n.kind]));
  const up = new Map<string, Set<string>>();    // node → the things it depends on (its sources)
  const down = new Map<string, Set<string>>();   // node → the things that depend on it (its impact)
  const add = (m: Map<string, Set<string>>, k: string, v: string) => { const s = m.get(k); if (s) s.add(v); else m.set(k, new Set([v])); };
  for (const e of graph?.edges ?? []) {
    // The table→measure 'contains' edge is structural (a measure's HOME table), not a data-flow dependency — keeping it
    // would hang measures directly off their table. Drop it so the tree flows table → column → measure (table→column
    // via 'contains', then measure→column via 'dependsOn'). A measure that genuinely references a table (e.g.
    // COUNTROWS) still links via its own 'dependsOn' edge, so real table dependencies are preserved.
    if (e.kind === 'contains' && kind.get(e.to) === 'measure') continue;
    let dependant: string, dependency: string;
    if (e.kind === 'source' || e.kind === 'contains') { dependant = e.to; dependency = e.from; }
    else { dependant = e.from; dependency = e.to; }   // dependsOn, relationship
    add(up, dependant, dependency);
    add(down, dependency, dependant);
  }
  return { up, down };
}

// The set of nodes to mark "expanded" so the tree reveals `depth` hops from root (root + everything within depth-1
// hops — a node must be expanded for ITS children to show). depth=2 shows e.g. table → column → measure; a big depth
// = "expand all" (capped so a huge model can't render thousands of cards at once).
function expandToDepth(root: string, adj: Map<string, Set<string>>, depth: number, cap = 600): Set<string> {
  const exp = new Set<string>([root]);
  let frontier = [root];
  for (let d = 1; d < depth && exp.size < cap; d++) {
    const next: string[] = [];
    for (const u of frontier) for (const v of adj.get(u) ?? []) if (!exp.has(v)) { exp.add(v); next.push(v); if (exp.size >= cap) break; }
    if (!next.length) break;
    frontier = next;
  }
  return exp;
}

function dagreLayout(ids: string[], edges: [string, string][], rankdir: 'LR' | 'TB', sizeOf: (id: string) => { w: number; h: number }): Map<string, { x: number; y: number }> {
  const g = new dagre.graphlib.Graph();
  g.setDefaultEdgeLabel(() => ({}));
  g.setGraph({ rankdir, nodesep: rankdir === 'TB' ? 18 : 22, ranksep: rankdir === 'TB' ? 70 : 90, marginx: 24, marginy: 24 });
  ids.forEach((id) => { const s = sizeOf(id); g.setNode(id, { width: s.w, height: s.h }); });
  const seenEdge = new Set<string>();
  edges.forEach(([s, t]) => { const k = s + '' + t; if (s !== t && !seenEdge.has(k)) { seenEdge.add(k); g.setEdge(s, t); } });
  dagre.layout(g);
  const m = new Map<string, { x: number; y: number }>();
  ids.forEach((id) => { const p = g.node(id); const s = sizeOf(id); m.set(id, { x: (p?.x ?? 0) - s.w / 2, y: (p?.y ?? 0) - s.h / 2 }); });
  return m;
}

const VERDICT_COLOR: Record<string, string> = { safe: '#36c98b', usedByUnusedOnly: '#e0b341', caution: '#e0654b' };

// Edge styling for the current selection — emphasise the selected node's incident edges, dim the rest. Shared by the
// layout build (so a rebuilt graph already reflects selection) and the decoration effect (so a click restyles without
// a relayout), keeping the two in agreement.
function edgeStyle(sel: string | null, source: string, target: string): React.CSSProperties {
  const on = !sel || source === sel || target === sel;
  return { stroke: on && sel ? 'var(--sem-accent)' : 'var(--sem-border)', strokeWidth: on && sel ? 2 : 1.4, opacity: sel && !on ? 0.25 : 1 };
}

type CardData = {
  node: LineageNode; hasChildren: boolean; isExpanded: boolean; isRoot: boolean; selected: boolean;
  childCount: number; verdict?: string; orient: 'LR' | 'TB';
  cb: { toggle: (ref: string) => void; reroot: (ref: string) => void; impact: (ref: string) => void };
};
// A synthetic "+N more" node — the tail of a fan-out that's capped to keep any single rank readable (progressive
// disclosure: reveal the rest only on demand). Clicking it shows all of the parent's children.
type MoreData = { parent: string; count: number; orient: 'LR' | 'TB'; cb: { expandMore: (ref: string) => void } };

function handles(orient: 'LR' | 'TB') {
  return { tgt: orient === 'TB' ? Position.Top : Position.Left, src: orient === 'TB' ? Position.Bottom : Position.Right };
}

function MoreNode({ data }: NodeProps<Node<MoreData>>) {
  const h = handles(data.orient);
  return (
    <div className="rounded-lg border flex items-center justify-center nodrag" onClick={(e) => { e.stopPropagation(); data.cb.expandMore(data.parent); }}
      title={`Show ${data.count} more`} style={{
        width: NODE_W, minHeight: 30, padding: '4px 8px', cursor: 'pointer',
        background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', borderStyle: 'dashed',
        color: 'var(--sem-accent)', fontSize: 11, fontWeight: 600,
      }}>
      <Handle type="target" position={h.tgt} isConnectable={false} style={{ opacity: 0 }} />
      +{data.count} more…
    </div>
  );
}

function TreeNode({ data }: NodeProps<Node<CardData>>) {
  const { node, hasChildren, isExpanded, isRoot, selected, childCount, verdict, orient, cb } = data;
  const color = resolveColor(KIND_COLOR[node.kind], '#9aa0aa');
  const h = handles(orient);
  return (
    <div className="rounded-lg border flex flex-col justify-center" style={{
      width: NODE_W, minHeight: NODE_H, padding: '6px 8px', background: 'var(--sem-surface)',
      borderColor: selected || isRoot ? 'var(--sem-accent)' : 'var(--sem-border)',
      boxShadow: selected ? '0 0 0 1px var(--sem-accent)' : isRoot ? '0 0 0 1px var(--sem-accent)' : '0 1px 3px rgba(0,0,0,0.25)',
      opacity: node.isHidden ? 0.6 : 1,
    }}>
      <Handle type="target" position={h.tgt} isConnectable={false} style={{ opacity: 0 }} />
      <Handle type="source" position={h.src} isConnectable={false} style={{ opacity: 0 }} />
      <div className="flex items-center gap-1.5">
        {hasChildren ? (
          <button className="nodrag" onClick={(e) => { e.stopPropagation(); cb.toggle(node.ref); }} title={isExpanded ? 'Collapse' : `Expand (${childCount})`}
            style={{ background: 'transparent', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer', padding: 0, width: 12, fontSize: 10 }}>
            {isExpanded ? '▾' : '▸'}
          </button>
        ) : <span style={{ width: 12 }} />}
        <span className="shrink-0 text-[12px]" style={{ color }}>{KIND_GLYPH[node.kind] ?? '•'}</span>
        <span className="truncate text-[12px] font-medium" style={{ color: 'var(--sem-fg)' }} title={node.name}>{node.name}</span>
        {verdict && <span className="shrink-0 w-1.5 h-1.5 rounded-full" title={'safe-to-remove: ' + verdict} style={{ background: VERDICT_COLOR[verdict] ?? 'var(--sem-muted)' }} />}
        {hasChildren && !isExpanded && <span className="ml-auto shrink-0 text-[9px] tnum px-1 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>+{childCount}</span>}
      </div>
      <div className="flex items-center gap-1.5 mt-0.5 pl-[18px]">
        <span className="truncate text-[10px]" style={{ color: 'var(--sem-muted)' }}>{node.table ? node.table + ' · ' : ''}{KIND_LABEL[node.kind] ?? uiLabel(node.kind)}</span>
        {selected && (
          <span className="ml-auto flex gap-1">
            <button className="nodrag text-[9px] px-1 rounded" onClick={(e) => { e.stopPropagation(); cb.reroot(node.ref); }} title="Make this the root" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>root</button>
            <button className="nodrag text-[9px] px-1 rounded" onClick={(e) => { e.stopPropagation(); cb.impact(node.ref); }} title="Assess this change" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>assess</button>
          </span>
        )}
      </div>
    </div>
  );
}

const nodeTypes = { lin: TreeNode, more: MoreNode };
const FANOUT_CAP = 10;   // max children shown per node before a "+N more" tail — keeps any rank readable

function LineageTreeInner({ graph, unusedItems, onOpenImpact, modeSwitch, counts, caveat, error }: TreeViewProps) {
  const [root, setRoot] = useState<string | null>(null);
  const [dir, setDir] = useState<Dir>('downstream');
  const [orient, setOrient] = useState<'LR' | 'TB'>('LR');   // horizontal (left→right) or vertical (top→bottom)
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [showAll, setShowAll] = useState<Set<string>>(new Set());   // parents whose full (uncapped) child list is shown
  const [selected, setSelected] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [hiddenKinds, setHiddenKinds] = useState<Set<string>>(new Set());   // granularity filter — hide a kind (+ its branches)
  const [selTables, setSelTables] = useState<Set<string>>(new Set());   // slicer: prune the tree to a subset of tables (empty = all)
  const [selFields, setSelFields] = useState<Set<string>>(new Set());   // slicer: prune to specific measures/columns (empty = all)
  const [nodes, setNodes, onNodesChange] = useNodesState<Node<CardData>>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);
  const rf = useReactFlow();
  const [fillRef, treeH] = useFillHeight(440);   // the canvas fills the viewport height (dynamic to the window)
  // The row folds in priority order when it runs out of room: slicers first, then the direction and layout
  // pair, and only then the view switcher. Expand all, Collapse and Fit never fold - Fit is the control a
  // person reaches for the moment a resize leaves the tree looking wrong.
  const row = useToolRow(3);
  // True once the person has panned/zoomed since the LAST fit — a resize-triggered auto-fit checks this so it never
  // fights a manual view the person set up on purpose (see doFit / the canvas callback ref below).
  const interactedSinceFit = useRef(false);
  const doFit = useCallback((opts?: Parameters<typeof rf.fitView>[0]) => {
    try { rf.fitView(opts ?? { padding: 0.2, duration: 300 }); } catch { /* not mounted */ }
    interactedSinceFit.current = false;   // a completed fit is the new baseline the interaction guard measures from
  }, [rf]);
  // Fit only once React Flow has MEASURED the cards. `node.measured` is filled on a render pass after the nodes are
  // set, and a card's height is not known before that — so the single requestAnimationFrame this used to use fitted
  // against a zero/stale box and left the tree parked below the viewport (measured at 1000x800: the cards sat under
  // the canvas's bottom edge until Fit was pressed). Poll a few frames, then fit regardless, so a card that never
  // reports a size cannot mean no fit at all.
  const fitWhenMeasured = useCallback((opts?: Parameters<typeof rf.fitView>[0]) => {
    let tries = 0;
    const tick = () => {
      const ns = rf.getNodes();
      const ready = ns.length > 0 && ns.every((n) => (n.measured?.width ?? 0) > 0 && (n.measured?.height ?? 0) > 0);
      if (ready || tries++ > 30) { doFit(opts); return; }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }, [rf, doFit]);

  // Refit when the CANVAS resizes (a narrow pane, a window resize, or a VS Code panel drag) — otherwise the tree keeps
  // whatever pan/zoom it had for the OLD size and cards end up sitting partly outside the new viewport until Fit is
  // pressed by hand. Debounced (150ms) so a continuous drag-resize doesn't refit every frame, and gated by
  // interactedSinceFit so a resize never undoes a view the person panned/zoomed into on purpose.
  //
  // The observer is attached from a CALLBACK REF, not a mount effect, and that is the whole fix. The canvas is NOT in
  // this component's first render: `graph` arrives async, so the first paint is the "Loading the lineage tree…" panel
  // with no canvas element at all. A `useEffect(..., [])` therefore read a null ref, returned, and — having no deps to
  // re-run on — never attached an observer for the entire life of that first visit. Measured on the built bundle,
  // fresh Model > Lineage > Tree at 1440x900 resized to 1000x800: both cards finished outside the canvas (Margin's box
  // reached x=1186, y=738 against a canvas ending at x=983, y=711), while the identical resize after a Graph→Tree
  // remount was clean — because a remount renders the canvas on the FIRST render, so the old effect's ref was set.
  // A callback ref fires the instant the element attaches, whenever that turns out to be. (Same class of bug and the
  // same remedy as useFillHeight's callback ref in lineagetypes.ts.)
  const roRef = useRef<ResizeObserver | null>(null);
  const roTimer = useRef<number | null>(null);
  const [paneBox, setPaneBox] = useState({ w: 0, h: 0, inset: 0 });
  // Read the live helpers through refs so the callback ref itself can have EMPTY deps. A ref whose identity changes is
  // re-invoked by React with null and then the element on every render, which would disconnect/reconnect the observer
  // (and fire its initial callback) each time — a refit storm dressed up as a resize.
  const fillRefLive = useRef(fillRef); fillRefLive.current = fillRef;
  const fitLive = useRef(fitWhenMeasured); fitLive.current = fitWhenMeasured;
  const canvasRef = useCallback((el: HTMLDivElement | null) => {
    fillRefLive.current(el);   // useFillHeight still owns the pane's height
    roRef.current?.disconnect(); roRef.current = null;
    if (roTimer.current != null) { window.clearTimeout(roTimer.current); roTimer.current = null; }
    if (!el) { setPaneBox({ w: 0, h: 0, inset: 0 }); return; }
    // The same observer also reports how far the pane runs behind the page's bottom bar, so the in-canvas chips
    // can sit clear of the strip nobody can see (see toolrow.tsx).
    const measure = () => setPaneBox((p) => {
      const inset = visibleBottomInset(el);
      return (p.w === el.clientWidth && p.h === el.clientHeight && p.inset === inset) ? p : { w: el.clientWidth, h: el.clientHeight, inset };
    });
    measure();
    const ro = new ResizeObserver(() => {
      measure();
      if (roTimer.current != null) window.clearTimeout(roTimer.current);
      roTimer.current = window.setTimeout(() => { roTimer.current = null; if (!interactedSinceFit.current) fitLive.current({ padding: 0.2, duration: 200 }); }, 150);
    });
    ro.observe(el);
    roRef.current = ro;
  }, []);
  // A stable callback ref is not re-invoked on re-render, so unmount is the only place left to tear the observer down.
  useEffect(() => () => { roRef.current?.disconnect(); if (roTimer.current != null) window.clearTimeout(roTimer.current); }, []);

  // The minimap is an overview in a CORNER, not a curtain. React Flow draws it at a fixed ~200x150 whatever the pane
  // is, and fitView spreads the graph across the whole pane, so on a short pane the overlay lands on real cards:
  // measured on the built bundle at 1000x800 the pane is 966x200 and the 202x152 minimap covers 16% of its width but
  // 76% of its height, sitting on the Margin card even after a correct Fit. Hide it once it stops being a corner —
  // when its own box is more than ~12% of the pane's area. Measured either side of that line with room to spare: 6.2%
  // of the pane at 1440x900 (shown), 15.9% at 1000x800 (hidden). The graph, the zoom Controls and Fit all stay.
  const showMiniMap = paneBox.w > 0 && paneBox.h > 0 && (MINIMAP_BOX / (paneBox.w * paneBox.h)) <= 0.12;

  const nodeByRef = useMemo(() => new Map((graph?.nodes ?? []).map((n) => [n.ref, n])), [graph]);
  // Kinds present, ordered coarse→fine for the granularity filter (tables → columns → measures → reports → …).
  const GRAIN_ORDER = ['source', 'table', 'calcTable', 'column', 'calcColumn', 'hierarchy', 'measure', 'calcGroup', 'calcitem', 'relationship', 'report', 'page', 'visual'];
  const kindsPresent = useMemo(() => {
    const s = new Set((graph?.nodes ?? []).map((n) => n.kind));
    return GRAIN_ORDER.filter((k) => s.has(k)).concat([...s].filter((k) => !GRAIN_ORDER.includes(k)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [graph]);
  const hiddenKindsKey = [...hiddenKinds].sort().join(',');
  // Slicer options from the WHOLE model so the dropdowns always list everything (the tree only shows a rooted subset).
  const tableOptions = useMemo(() => tableFacetOptions(graph?.nodes ?? []), [graph]);
  const fieldOptions = useMemo(() => fieldFacetOptions(graph?.nodes ?? []), [graph]);
  const tablesKey = [...selTables].sort().join(',');
  const fieldsKey = [...selFields].sort().join(',');
  const sig = useMemo(() => {
    const es = graph?.edges ?? [];
    return (graph?.nodes ?? []).length + '/' + es.length + '|' + es.map((e) => e.from + '>' + e.to + ':' + e.kind).join(',');
  }, [graph]);
  const { up, down } = useMemo(() => buildDeps(graph), [sig]);   // eslint-disable-line react-hooks/exhaustive-deps
  const verdictByRef = useMemo(() => new Map((unusedItems ?? []).map((i) => [i.ref, i.verdict])), [unusedItems]);
  // Read selection / verdicts / the impact callback via refs INSIDE the layout effect so they seed the rebuilt nodes
  // WITHOUT being effect deps — only genuinely structural inputs (sig/root/dir/expandedKey) trigger a relayout+refit.
  // (A benign model/didChange churns verdictByRef + the parent's inline onOpenImpact; keeping them out of the deps
  // stops a relayout that would wipe the user's pan/zoom — and the decoration effect below re-applies them live.)
  const selectedRef = useRef(selected); selectedRef.current = selected;
  const verdictRef = useRef(verdictByRef); verdictRef.current = verdictByRef;
  const onImpactRef = useRef(onOpenImpact); onImpactRef.current = onOpenImpact;
  // Live dir + dependency maps for the stable cb (so reroot can seed a deeper expansion without being a layout-effect dep).
  const dirRef = useRef(dir); dirRef.current = dir;
  const depsRef = useRef({ up, down }); depsRef.current = { up, down };
  const adjFor = (d: Dir) => (d === 'downstream' ? down : up);

  // auto-pick a root when none is set / the current root vanished (highest-degree measure, else highest-degree node).
  // Seed a 2-hop expansion so the chain is visible immediately (e.g. table → column → measure) rather than one level.
  useEffect(() => {
    if (!graph || (root && nodeByRef.has(root))) return;
    const deg = new Map<string, number>();
    for (const e of graph.edges) { deg.set(e.from, (deg.get(e.from) ?? 0) + 1); deg.set(e.to, (deg.get(e.to) ?? 0) + 1); }
    const cand = graph.nodes.slice().sort((a, b) => (deg.get(b.ref) ?? 0) - (deg.get(a.ref) ?? 0));
    const pick = cand.find((n) => n.kind === 'measure') ?? cand[0];
    if (pick) { setRoot(pick.ref); setExpanded(expandToDepth(pick.ref, adjFor(dir), 2)); setSelected(null); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [graph, root, nodeByRef]);

  const cb = useMemo(() => ({
    toggle: (ref: string) => setExpanded((s) => { const n = new Set(s); n.has(ref) ? n.delete(ref) : n.add(ref); return n; }),
    reroot: (ref: string) => { setRoot(ref); setExpanded(expandToDepth(ref, (dirRef.current === 'downstream' ? depsRef.current.down : depsRef.current.up), 2)); setShowAll(new Set()); setSelected(ref); },
    impact: (ref: string) => onImpactRef.current(ref),
    expandMore: (ref: string) => setShowAll((s) => { const n = new Set(s); n.add(ref); return n; }),
  }), []);   // stable — reads the live impact callback / dir / deps through refs so it's never a layout-effect dep

  // Reveal the WHOLE chain from the root (table → column → measure → report page → visual, when reports are merged) in
  // one click, and collapse back to just the root. Expand-all is capped (see expandToDepth) so a huge model stays sane.
  const expandAll = () => { if (root) setExpanded(expandToDepth(root, adjFor(dir), 99)); };
  const collapseAll = () => { if (root) { setExpanded(new Set([root])); setShowAll(new Set()); } };

  const expandedKey = [...expanded].sort().join(',');
  const showAllKey = [...showAll].sort().join(',');

  // Build the visible subgraph (lazy reveal) + lay it out. Re-runs only on structural inputs — NOT on selection.
  useEffect(() => {
    if (!graph || !root || !nodeByRef.has(root)) { setNodes([]); setEdges([]); return; }
    const adj = dir === 'downstream' ? down : up;
    const scopeSel = makeScope(selTables, selFields);   // slicer prune — the root is always shown (the anchor), children are gated
    const visible = new Set<string>([root]);
    const processed = new Set<string>();
    const elist: [string, string][] = [];
    const moreNodes: { id: string; parent: string; count: number }[] = [];
    const queue = [root];
    while (queue.length) {
      const u = queue.shift()!;
      if (processed.has(u)) continue;
      processed.add(u);
      if (u !== root && !expanded.has(u)) continue;   // only the root + explicitly-expanded nodes reveal their children
      // Cap the fan-out: show the first FANOUT_CAP children, then a single "+N more" node (unless this parent was
      // "show all"-ed). Keeps any one rank readable — the core cognitive-load guard + high→low progressive disclosure.
      // The granularity filter prunes children whose KIND is hidden (and thus their branches).
      const kids = [...(adj.get(u) ?? [])].filter((v) => { const nd = nodeByRef.get(v); return !!nd && !hiddenKinds.has(nd.kind) && scopeSel.has(nd); });
      const capped = kids.length > FANOUT_CAP && !showAll.has(u);
      const shownKids = capped ? kids.slice(0, FANOUT_CAP) : kids;
      for (const v of shownKids) {
        elist.push([u, v]);
        if (!visible.has(v)) { visible.add(v); queue.push(v); }
      }
      if (capped) { const mid = 'more:' + u; moreNodes.push({ id: mid, parent: u, count: kids.length - FANOUT_CAP }); elist.push([u, mid]); }
    }
    const moreSet = new Set(moreNodes.map((m) => m.id));
    const ids = [...visible, ...moreNodes.map((m) => m.id)];
    const sizeOf = (id: string) => (moreSet.has(id) ? { w: NODE_W, h: 30 } : { w: NODE_W, h: NODE_H });
    const pos = dagreLayout(ids, elist, orient, sizeOf);
    const rfNodes: Node<CardData | MoreData>[] = [...visible].map((ref) => {
      const node = nodeByRef.get(ref)!;
      const kids = adj.get(ref);
      return {
        id: ref, type: 'lin', position: pos.get(ref) ?? { x: 0, y: 0 },
        data: {
          node, hasChildren: !!kids && kids.size > 0, isExpanded: expanded.has(ref) || ref === root, isRoot: ref === root,
          selected: ref === selectedRef.current, childCount: kids?.size ?? 0, verdict: verdictRef.current.get(ref), orient, cb,
        },
      } as Node<CardData>;
    });
    for (const m of moreNodes) rfNodes.push({ id: m.id, type: 'more', position: pos.get(m.id) ?? { x: 0, y: 0 }, data: { parent: m.parent, count: m.count, orient, cb } } as Node<MoreData>);
    const seen = new Set<string>();
    const rfEdges: Edge[] = [];
    for (const [s, t] of elist) {
      const id = s + '>' + t; if (seen.has(id)) continue; seen.add(id);
      const isMore = moreSet.has(t);
      rfEdges.push({ id, source: s, target: t, type: 'default', animated: false, style: isMore ? { stroke: 'var(--sem-border)', strokeWidth: 1.4, strokeDasharray: '4 3' } : edgeStyle(selectedRef.current, s, t), markerEnd: isMore ? undefined : { type: 'arrowclosed' as any, color: '#9aa0aa', width: 14, height: 14 } });
    }
    setNodes(rfNodes as Node<CardData>[]);
    setEdges(rfEdges);
    // fit after the DOM updates — only here (structural changes: new root / direction / orient / expand / show-more)
    fitWhenMeasured();
  }, [sig, root, dir, orient, expandedKey, showAllKey, hiddenKindsKey, tablesKey, fieldsKey, cb]);   // eslint-disable-line react-hooks/exhaustive-deps

  // Decoration WITHOUT a relayout/refit: re-apply selection highlight + safe-to-remove verdict onto the existing nodes
  // & edges. Runs on a selection click OR a (benign) verdict refresh — neither should move the graph or reset the view.
  useEffect(() => {
    setNodes((nds) => nds.map((n) => {
      if (n.type === 'more') return n;   // synthetic nodes carry no selection/verdict
      const sel = n.id === selected, v = verdictByRef.get(n.id);
      return (n.data.selected === sel && n.data.verdict === v) ? n : { ...n, data: { ...n.data, selected: sel, verdict: v } };
    }));
    setEdges((eds) => eds.map((e) => (e.target.startsWith('more:') ? e : { ...e, style: edgeStyle(selected, e.source, e.target) })));
  }, [selected, verdictByRef, setNodes, setEdges]);

  const matches = useMemo(() => rankNodeMatches(graph?.nodes ?? [], search), [search, graph]);
  const toggleKind = (k: string) => setHiddenKinds((s) => { const n = new Set(s); n.has(k) ? n.delete(k) : n.add(k); return n; });

  const rootNode = root ? nodeByRef.get(root) : null;

  if (!graph) return <div className="p-6 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading the lineage tree…</div>;
  if ((graph.nodes?.length ?? 0) === 0) return <div className="p-6 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Nothing to trace yet. Open a model first.</div>;

  const searchBox = (
    <div className="relative">
      <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search to start from" spellCheck={false}
        aria-label="Start from a measure, table or column"
        onKeyDown={(e) => { if (e.key === 'Enter' && matches[0]) { cb.reroot(matches[0].ref); setSearch(''); } else if (e.key === 'Escape') setSearch(''); }}
        className="sem-toolrow-input" style={{ width: 168, borderColor: 'var(--sem-accent)' }} />
      {matches.length > 0 && (
        <div className="absolute z-20 mt-1 rounded-md overflow-hidden" style={{ width: 240, background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', boxShadow: '0 6px 20px rgba(0,0,0,0.4)' }}>
          {matches.map((m) => (
            <button key={m.ref} onMouseDown={(e) => { e.preventDefault(); cb.reroot(m.ref); setSearch(''); }}
              className="flex items-center gap-2 w-full text-left px-2 py-1 text-[12px] hover:bg-[var(--sem-surface-2)]">
              <span style={{ color: resolveColor(KIND_COLOR[m.kind], '#9aa0aa') }}>{KIND_GLYPH[m.kind] ?? '•'}</span>
              <span className="truncate flex-1">{m.name}</span>
              {m.table && <span className="text-[10px] truncate" style={{ color: 'var(--sem-muted)', maxWidth: 80 }}>{m.table}</span>}
            </button>
          ))}
        </div>
      )}
    </div>
  );
  const directionControls = (
    <>
      <ToolBtn active={dir === 'upstream'} onClick={() => setDir('upstream')} title="What this is built from">↑ Upstream</ToolBtn>
      <ToolBtn active={dir === 'downstream'} onClick={() => setDir('downstream')} title="What relies on this">↓ Downstream</ToolBtn>
      <ToolBtn active={orient === 'LR'} onClick={() => setOrient('LR')} title="Lay the steps out left to right">⇄ Horizontal</ToolBtn>
      <ToolBtn active={orient === 'TB'} onClick={() => setOrient('TB')} title="Lay the steps out top to bottom">⇅ Vertical</ToolBtn>
    </>
  );
  const sliceControls = (
    <>
      <MultiSelect label="Tables" options={tableOptions} selected={selTables} onChange={setSelTables} width={220} title="Cut the tree down to some of the tables" />
      <MultiSelect label="Fields" options={fieldOptions} selected={selFields} onChange={setSelFields} width={260} title="Cut the tree down to certain measures or columns" />
      {(selTables.size > 0 || selFields.size > 0) && <ToolBtn onClick={() => { setSelTables(new Set()); setSelFields(new Set()); }} title="Show everything again">Clear</ToolBtn>}
    </>
  );
  // The kinds are a list a model decides the length of, so they are ALWAYS one menu. Spelling eight of them out
  // is what used to push the tree's controls onto a second row.
  const kindMenu = kindsPresent.length > 1 ? (
    <RowMenu label="Show" align="end" badge={hiddenKinds.size || undefined} title="Choose which kinds of thing the tree shows">
      {kindsPresent.map((k) => {
        const on = !hiddenKinds.has(k);
        return (
          <button key={k} className="sem-btn sem-btn-sm" aria-pressed={on} onClick={() => toggleKind(k)}
            title={(on ? 'Hide ' : 'Show ') + (KIND_LABEL[k] ?? k)} style={{ opacity: on ? 1 : 0.55 }}>
            <span style={{ color: resolveColor(KIND_COLOR[k], '#9aa0aa') }}>{KIND_GLYPH[k] ?? '•'}</span>
            {KIND_LABEL[k] ?? k}
          </button>
        );
      })}
    </RowMenu>
  ) : null;

  return (
    <div className="h-full flex flex-col">
      {/* ONE tool row. It used to be two: a "Dependency tree" strip with the search box and the layout buttons,
          and a "slice / show" strip under it. The root, the counts and the model-only caveat are chips inside
          the canvas now, and the groups fold into menus in priority order before anything can wrap. */}
      <ToolRow rowRef={row.ref}>
        {row.level >= 3
          ? <RowMenu label="Tree" title="Switch to another view of lineage">{modeSwitch}</RowMenu>
          : modeSwitch}
        <RowSep />
        {searchBox}
        <RowSep />
        {row.level >= 2
          ? <RowMenu label="Direction" title="Which way to follow the chain, and how to lay it out">{directionControls}</RowMenu>
          : directionControls}
        <RowSep />
        <ToolBtn onClick={expandAll} title="Open the whole chain from the starting point">Expand all</ToolBtn>
        <ToolBtn onClick={collapseAll} title="Close everything back to the starting point">Collapse</ToolBtn>
        <ToolBtn onClick={() => doFit()} title="Fit the whole tree in view">Fit</ToolBtn>
        <RowSep />
        {row.level >= 1
          ? <RowMenu label="Slice" align="end" badge={(selTables.size + selFields.size) || undefined} title="Cut the tree down to part of the model">{sliceControls}</RowMenu>
          : sliceControls}
        {kindMenu}
      </ToolRow>

      <div ref={canvasRef} style={{ height: treeH, position: 'relative', ...canvasInsetStyle(paneBox.inset) }}>
        <ReactFlow
          nodes={nodes} edges={edges} onNodesChange={onNodesChange} nodeTypes={nodeTypes}
          onNodeClick={(_, n) => setSelected(n.id)} onPaneClick={() => setSelected(null)}
          onMove={(evt) => { if (evt) interactedSinceFit.current = true; }}   // evt is null for our own programmatic fitView calls, non-null for a real user drag/wheel
          fitView fitViewOptions={{ padding: 0.2 }} minZoom={0.2} maxZoom={2} proOptions={{ hideAttribution: true }}
          defaultEdgeOptions={{ type: 'default' }} nodesConnectable={false} elementsSelectable>
          <Background gap={20} color="rgba(140,140,160,0.12)" />
          <Controls showInteractive={false} />
          {showMiniMap && <MiniMap pannable zoomable nodeColor={(n) => resolveColor(KIND_COLOR[(n.data as CardData)?.node?.kind], '#9aa0aa')} maskColor="rgba(0,0,0,0.5)" style={{ background: 'var(--sem-surface-2)' }} />}
        </ReactFlow>
        {/* The honesty caveat keeps its own words and its own room. Truncating a warning into a tooltip would
            hide the very thing it exists to say, so this is the one chip allowed to run to several lines. */}
        {error && <CanvasChip at="top" tone="bad" note>{error}</CanvasChip>}
        {!error && caveat && <CanvasChip at="top" tone="warn" note>{caveat}</CanvasChip>}
        {/* Status and counts, which used to be a label and a chip on the row above. */}
        <CanvasChip at="start" title={paneBox.h >= CANVAS_CHIP_MIN_H ? undefined : (counts ?? undefined)}>
          {rootNode
            ? <span>Starting from <span style={{ color: resolveColor(KIND_COLOR[rootNode.kind], '#9aa0aa') }}>{KIND_GLYPH[rootNode.kind] ?? '•'}</span> <b>{rootNode.name}</b></span>
            : <span>No starting point picked yet</span>}
          <span>{dir === 'upstream' ? 'what it is built from' : 'what relies on it'}</span>
        </CanvasChip>
        {counts && paneBox.h >= CANVAS_CHIP_MIN_H && !showMiniMap && <CanvasChip at="end">{counts}</CanvasChip>}
        {nodes.length === 0 && <div className="absolute inset-0 flex items-center justify-center text-[12px]" style={{ color: 'var(--sem-muted)' }}>Search for a field above, or pick one, to trace it.</div>}
      </div>
    </div>
  );
}

export interface TreeViewProps {
  graph: LineageResult | null;
  unusedItems: UnusedLite[] | undefined;
  onOpenImpact: (ref: string) => void;
  // The Lineage view switcher, owned by lineage.tsx. It leads this view's single tool row.
  modeSwitch?: React.ReactNode;
  counts?: string | null;
  caveat?: string | null;
  error?: string | null;
}

export function LineageTreeView(props: TreeViewProps) {
  return <ReactFlowProvider><LineageTreeInner {...props} /></ReactFlowProvider>;
}

// ---- local primitives (consistent with lineage.tsx) ------------------------------------------
// Every control on the row takes the shared dense scale, so the tool row holds one control height, not three.
function ToolBtn({ active, onClick, children, title }: { active?: boolean; onClick: () => void; children: React.ReactNode; title?: string }) {
  return <button className="sem-btn sem-btn-sm" onClick={onClick} title={title} aria-pressed={active}>{children}</button>;
}
