import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ReactFlow, ReactFlowProvider, Background, Controls, MiniMap, Handle, Position,
  useNodesState, useEdgesState, useReactFlow, type Node, type Edge, type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import dagre from '@dagrejs/dagre';
import { KIND_COLOR, KIND_GLYPH, KIND_LABEL, fieldFacetOptions, makeScope, rankNodeMatches, resolveColor, tableFacetOptions, useFillHeight, type LineageNode, type LineageResult } from './lineagetypes';
import { MultiSelect } from './lineageslicer';
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

function LineageTreeInner({ graph, unusedItems, onOpenImpact }: { graph: LineageResult | null; unusedItems: UnusedLite[] | undefined; onOpenImpact: (ref: string) => void }) {
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
    requestAnimationFrame(() => { try { rf.fitView({ padding: 0.2, duration: 300 }); } catch { /* not mounted */ } });
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

  if (!graph) return <Panel><Empty>Loading the lineage tree…</Empty></Panel>;
  if ((graph.nodes?.length ?? 0) === 0) return <Panel><Empty>No objects yet. Open a model.</Empty></Panel>;

  return (
    <Panel className="p-0 overflow-hidden">
      <div className="flex items-center gap-2 px-3 py-2 border-b flex-wrap" style={{ borderColor: 'var(--sem-border)' }}>
        <span className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Dependency tree</span>
        <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>Start from</span>
        <div className="relative">
          <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="pick a measure, table or column…" spellCheck={false}
            onKeyDown={(e) => { if (e.key === 'Enter' && matches[0]) { cb.reroot(matches[0].ref); setSearch(''); } else if (e.key === 'Escape') setSearch(''); }}
            className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ width: 230, background: 'var(--sem-bg)', color: 'var(--sem-fg)', border: '1px solid var(--sem-accent)' }} />
          {matches.length > 0 && (
            <div className="absolute z-10 mt-1 rounded-md overflow-hidden" style={{ width: 240, background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', boxShadow: '0 6px 20px rgba(0,0,0,0.4)' }}>
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
        {rootNode && (
          <span className="flex items-center gap-1 text-[11px] px-2 py-0.5 rounded-md" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }} title="The current starting point. Type above or click a node's “root” button to change it">
            root:
            <span style={{ color: resolveColor(KIND_COLOR[rootNode.kind], '#9aa0aa') }}>{KIND_GLYPH[rootNode.kind] ?? '•'}</span>
            <span className="font-medium truncate" style={{ color: 'var(--sem-fg)', maxWidth: 160 }}>{rootNode.name}</span>
          </span>
        )}
        <div className="ml-auto flex items-center gap-1.5">
          <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>direction</span>
          <ToolBtn active={dir === 'upstream'} onClick={() => setDir('upstream')} title="What this is built from (its sources)">↑ Upstream</ToolBtn>
          <ToolBtn active={dir === 'downstream'} onClick={() => setDir('downstream')} title="What depends on this (impact)">↓ Downstream</ToolBtn>
          <span className="w-px h-4 mx-0.5" style={{ background: 'var(--sem-border)' }} />
          <ToolBtn active={orient === 'LR'} onClick={() => setOrient('LR')} title="Horizontal: ranks flow left → right">⇄ Horizontal</ToolBtn>
          <ToolBtn active={orient === 'TB'} onClick={() => setOrient('TB')} title="Vertical: ranks flow top → bottom">⇅ Vertical</ToolBtn>
          <span className="w-px h-4 mx-0.5" style={{ background: 'var(--sem-border)' }} />
          <ToolBtn onClick={expandAll} title="Reveal the whole chain from the root">Expand all</ToolBtn>
          <ToolBtn onClick={collapseAll} title="Collapse back to the root">Collapse</ToolBtn>
          <ToolBtn onClick={() => { try { rf.fitView({ padding: 0.2, duration: 300 }); } catch { /* */ } }}>Fit</ToolBtn>
        </div>
      </div>

      {/* Filter bar — SLICE the tree to a subset of tables / specific fields (multi-select), and set GRANULARITY by
          hiding a kind (+ its branches) to move high-level (tables) → detail (+columns, +measures, +reports). Both
          reduce cognitive load: pick the corner of the model you care about, see the shape first, then add grain. */}
      <div className="flex items-center gap-1.5 px-3 py-1 border-b flex-wrap" style={{ borderColor: 'var(--sem-border)' }}>
        <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>slice</span>
        <MultiSelect label="Tables" options={tableOptions} selected={selTables} onChange={setSelTables} width={220} title="Prune the tree to a subset of tables" />
        <MultiSelect label="Fields" options={fieldOptions} selected={selFields} onChange={setSelFields} width={260} title="Prune the tree to specific measures / columns" />
        {(selTables.size > 0 || selFields.size > 0) && <ToolBtn onClick={() => { setSelTables(new Set()); setSelFields(new Set()); }} title="Clear the slicers">Clear slice</ToolBtn>}
        {kindsPresent.length > 1 && (
          <>
            <span className="w-px h-4 mx-1" style={{ background: 'var(--sem-border)' }} />
            <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>show</span>
          {kindsPresent.map((k) => {
            const on = !hiddenKinds.has(k);
            return (
              <button key={k} onClick={() => toggleKind(k)} title={(on ? 'Hide ' : 'Show ') + (KIND_LABEL[k] ?? k) + ' nodes'}
                className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded-md transition-[transform,filter] duration-100 active:scale-90"
                style={{ background: on ? 'var(--sem-bg)' : 'transparent', border: '1px solid var(--sem-border)', color: on ? 'var(--sem-fg)' : 'var(--sem-muted)', opacity: on ? 1 : 0.5 }}>
                <span style={{ color: resolveColor(KIND_COLOR[k], '#9aa0aa') }}>{KIND_GLYPH[k] ?? '•'}</span>
                {KIND_LABEL[k] ?? k}
              </button>
            );
          })}
          </>
        )}
      </div>

      <div ref={fillRef} style={{ height: treeH, position: 'relative' }}>
        <ReactFlow
          nodes={nodes} edges={edges} onNodesChange={onNodesChange} nodeTypes={nodeTypes}
          onNodeClick={(_, n) => setSelected(n.id)} onPaneClick={() => setSelected(null)}
          fitView fitViewOptions={{ padding: 0.2 }} minZoom={0.2} maxZoom={2} proOptions={{ hideAttribution: true }}
          defaultEdgeOptions={{ type: 'default' }} nodesConnectable={false} elementsSelectable>
          <Background gap={20} color="rgba(140,140,160,0.12)" />
          <Controls showInteractive={false} />
          <MiniMap pannable zoomable nodeColor={(n) => resolveColor(KIND_COLOR[(n.data as CardData)?.node?.kind], '#9aa0aa')} maskColor="rgba(0,0,0,0.5)" style={{ background: 'var(--sem-surface-2)' }} />
        </ReactFlow>
        {nodes.length === 0 && <div className="absolute inset-0 flex items-center justify-center text-[12px]" style={{ color: 'var(--sem-muted)' }}>Search or pick a root to trace its lineage.</div>}
      </div>
    </Panel>
  );
}

export function LineageTreeView(props: { graph: LineageResult | null; unusedItems: UnusedLite[] | undefined; onOpenImpact: (ref: string) => void }) {
  return <ReactFlowProvider><LineageTreeInner {...props} /></ReactFlowProvider>;
}

// ---- local primitives (consistent with lineage.tsx) ------------------------------------------
function Panel({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`rounded-xl border ${className.includes('p-0') ? '' : 'p-4'} ${className}`} style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function Empty({ children }: { children: React.ReactNode }) {
  return <div className="text-[12px] py-10 text-center" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}
function ToolBtn({ active, onClick, children, title }: { active?: boolean; onClick: () => void; children: React.ReactNode; title?: string }) {
  return (
    <button onClick={onClick} title={title} className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-[transform,filter,background-color] duration-100 active:scale-90 active:brightness-125 hover:brightness-110"
      style={active ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
