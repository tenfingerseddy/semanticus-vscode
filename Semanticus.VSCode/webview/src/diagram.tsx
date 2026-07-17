import { useEffect, useMemo, useRef, useState, useCallback } from 'react';
import {
  ReactFlow, ReactFlowProvider, Background, Controls, Handle, Position, ConnectionMode,
  NodeToolbar, useNodesState, useEdgesState, useReactFlow, useUpdateNodeInternals,
  type Node, type Edge, type NodeProps, type Connection,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import dagre from '@dagrejs/dagre';
import { rpc, onDidChange, onLayoutChange, loadState, saveState, requestDropTables, type LayoutData } from './bridge';
import { edgeTypes, RelConnectionLine } from './diagramedges';
import { RelationshipsView } from './relationships';
import type { ColumnRow } from './wire';
import { uiLabel } from './copy';

// Field-parameter accent: a restrained muted violet (Kane's "purple") that sits beside the Ink+Signal palette
// without competing with Signal green / date amber / calc teal. Solid swatch takes black text like the others.
const FIELD_PARAM_VIOLET = '#9B7EDE';

// wire types (camelCase, from Semanticus.Engine/Protocol.cs)
export interface GraphTable { ref: string; name: string; isHidden: boolean; isDateTable: boolean; isCalculated: boolean; isFieldParameter: boolean; columns: number; measures: number; keyColumns: string[]; hasDescription: boolean; }
export interface GraphRelationship { name: string; fromTable: string; fromColumn: string; toTable: string; toColumn: string; fromCardinality: string; toCardinality: string; crossFilter: string; isActive: boolean; }
export interface ModelGraph { tables: GraphTable[]; relationships: GraphRelationship[]; }

// Each table falls into exactly ONE kind for the diagram's type filters. A field parameter IS a calculated table,
// so FP takes precedence over calc (it matches the "Field params" filter, never "Calculated"); everything else is a
// regular data table. A date table is a data-CATEGORY, not a kind, so it counts as a regular data table here.
type TableKind = 'data' | 'calc' | 'fp';
const kindOf = (t: GraphTable): TableKind => (t.isFieldParameter ? 'fp' : t.isCalculated ? 'calc' : 'data');

const NODE_W = 190;   // minimum node width (collapsed)
const NODE_H = 92;    // minimum node height (collapsed)
const GRID = 20;   // snap-to-grid step (matches the Background dot gap)
// Dynamic node sizing: a node grows on the axis its relationship anchors run along, so many incident
// relationships spread into their own orthogonal lanes instead of bundling on a short fixed edge (the
// "collapsed lines" near a busy node). With frac=(i+1)/(n+1), n anchors on a side need (n+1)*SLOT of length
// to stay SLOT apart — so WIDTH grows with the Top/Bottom anchor count, HEIGHT with the Left/Right count.
const SLOT = 30;

// --- Saved diagrams ---------------------------------------------------------------------------
// A diagram is a named, curated subset of the model with remembered node positions. "All tables"
// (all:true) always shows every table (new model tables appear automatically); custom diagrams
// (all:false) show only `tables`. Both remember `positions`, so a layout survives reloads — the
// whole point of this canvas (the old auto-layout reset every time). Persisted via the webview's
// saveState (vscode.setState), namespaced by key — see bridge.ts.
type XY = { x: number; y: number };
type Size = { w: number; h: number };
type SizeOf = (name: string) => Size;   // a node's dynamic box, derived from its anchors for the active layout
type LayoutMode = 'free' | 'hierarchy' | 'busmatrix' | 'layered';
interface SavedDiagram { id: string; name: string; all: boolean; tables: string[]; positions: Record<string, XY>; layout?: LayoutMode; expanded?: Record<string, boolean>; }

const ALL_ID = 'all';
const DEFAULT_DIAGRAM: SavedDiagram = { id: ALL_ID, name: 'All tables', all: true, tables: [], positions: {} };
const uniq = (xs: string[]) => Array.from(new Set(xs));

// A relationship end's connection point on a node side. `frac` evenly spreads the K ends sharing a side into K
// distinct anchors so no two edges enter/exit at the same point. `rel` matches the edge's sourceHandle/targetHandle.
type Anchor = { rel: string; kind: 'source' | 'target'; side: Position; frac: number };
type Toast = { text: string; tone: 'ok' | 'error'; action?: { label: string; run: () => void | Promise<void> } };
const EMPTY_SET = new Set<string>();          // stable identities so a table with no related/anchor data doesn't churn
const EMPTY_ANCHORS: Anchor[] = [];

// The box a node needs so its anchors stay SLOT apart: width from the busier of its Top/Bottom edges, height
// from the busier of its Left/Right edges. A node with few/no relationships stays at the NODE_W×NODE_H minimum.
// Anchor sides depend on the layout (see computeAnchors), so size is always derived from THAT layout's anchors —
// keeping the rendered node, its handle spread, and the layout spacing in perfect agreement.
function sizeForAnchors(anchors: Anchor[]): Size {
  let top = 0, bottom = 0, left = 0, right = 0;
  for (const a of anchors) {
    if (a.side === Position.Top) top++;
    else if (a.side === Position.Bottom) bottom++;
    else if (a.side === Position.Left) left++;
    else right++;
  }
  const horiz = Math.max(top, bottom);   // anchors along a horizontal edge → need WIDTH to separate
  const vert = Math.max(left, right);    // anchors along a vertical edge → need HEIGHT to separate
  return { w: Math.max(NODE_W, (horiz + 1) * SLOT), h: Math.max(NODE_H, (vert + 1) * SLOT) };
}

type GraphCardData = {
  t: GraphTable; rels: number; curated: boolean;
  anchors: Anchor[]; w: number; h: number;   // dynamic size: grows with incident-relationship count per side
  expanded: boolean;
  columns: ColumnRow[] | null;   // the table's columns, only when expanded (for drag-to-create)
  related: Set<string>;          // column names already in a relationship — sorted to the top, above a divider
  filter: string;                // per-node column search text
  onAddRelated: (name: string) => void; onRemove: (name: string) => void;
  onToggleExpand: (name: string) => void; onFilter: (name: string, q: string) => void;
};

// Position an (invisible) relationship anchor handle along its node side: frac runs the long axis of the side.
function anchorStyle(side: Position, frac: number): React.CSSProperties {
  const base: React.CSSProperties = { width: 8, height: 8, opacity: 0, border: 'none', background: 'transparent' };
  return side === Position.Top || side === Position.Bottom ? { ...base, left: `${frac * 100}%` } : { ...base, top: `${frac * 100}%` };
}

function TableNode({ data, selected }: NodeProps<Node<GraphCardData>>) {
  const t = data.t;
  // Colour tables by role so the map reads at a glance: connected = Signal green, date = amber, field parameter =
  // muted violet, calculated = teal, isolated (no relationships) = blue (informational, not alarming — avoids a
  // red/green clash). A field parameter IS a calc table, so it must be tested before the plain-calc branch.
  const accent = t.isDateTable ? 'var(--sem-warn)' : t.isFieldParameter ? FIELD_PARAM_VIOLET : t.isCalculated ? '#17B3A3' : data.rels === 0 ? '#2E7BD0' : 'var(--sem-accent)';

  // Expanded column list: key + already-related columns first, then a divider, then the rest. Search filters across all.
  const cols = useMemo(() => {
    if (!data.expanded || !data.columns) return null;
    const q = data.filter.trim().toLowerCase();
    const matched = data.columns.filter((c) => !q || c.name.toLowerCase().includes(q));
    const isPrimary = (c: ColumnRow) => c.isKey || data.related.has(c.name);
    const byName = (a: ColumnRow, b: ColumnRow) => a.name.localeCompare(b.name);
    return { primary: matched.filter(isPrimary).sort(byName), rest: matched.filter((c) => !isPrimary(c)).sort(byName) };
  }, [data.expanded, data.columns, data.filter, data.related]);

  return (
    <div className="rounded-lg border text-[11px] flex flex-col" style={{
      width: data.w, minHeight: data.h, background: 'var(--sem-surface)',
      borderColor: selected ? 'var(--sem-accent)' : 'var(--sem-border)',
      boxShadow: selected ? '0 0 0 1px var(--sem-accent)' : '0 1px 3px rgba(0,0,0,0.25)',
    }}>
      {data.curated && (
        <NodeToolbar isVisible={selected} position={Position.Top} className="flex gap-1">
          <button onClick={() => data.onAddRelated(t.name)} title="Add tables related to this one" style={tbBtn}>＋ related</button>
          <button onClick={() => data.onRemove(t.name)} title="Remove from this diagram" style={tbBtn}>✕ remove</button>
        </NodeToolbar>
      )}
      {/* Relationship anchor handles — one per incident relationship end, invisible, pure routing geometry (not connectable). */}
      {data.anchors.map((a) => (
        <Handle key={a.rel + ':' + a.kind} id={a.rel + ':' + a.kind} type={a.kind} position={a.side} isConnectable={false} style={anchorStyle(a.side, a.frac)} />
      ))}
      <div className="px-2.5 py-1.5 rounded-t-lg flex items-center gap-1.5" style={{ borderBottom: '1px solid var(--sem-border)', background: 'color-mix(in srgb,' + accent + ' 16%, transparent)' }}>
        <button className="nodrag" onClick={() => data.onToggleExpand(t.name)} title={data.expanded ? 'Hide columns' : 'Show columns to connect'}
          style={{ background: 'transparent', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer', padding: 0, fontSize: 10, width: 12 }}>{data.expanded ? '▾' : '▸'}</button>
        <span className="w-1.5 h-1.5 rounded-full shrink-0" style={{ background: accent }} />
        <span className="font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{t.name}</span>
        <span className="ml-auto flex items-center gap-1 shrink-0">
          {t.isHidden && <span className="text-[9px] px-1 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }} title="Hidden from report view">hidden</span>}
          {t.isDateTable && <span className="text-[9px] px-1 rounded" style={{ background: 'var(--sem-warn)', color: '#000' }}>DATE</span>}
          {/* A field parameter is a calc table — show the more specific FP marker instead of CALC, never both. */}
          {t.isFieldParameter
            ? <span className="text-[9px] px-1 rounded" style={{ background: FIELD_PARAM_VIOLET, color: '#000' }} title="Field parameter">FP</span>
            : t.isCalculated && <span className="text-[9px] px-1 rounded" style={{ background: '#17B3A3', color: '#000' }}>CALC</span>}
        </span>
      </div>
      {!data.expanded ? (
        <div className="px-2.5 py-1.5 flex flex-col gap-0.5" style={{ color: 'var(--sem-muted)' }}>
          <div className="flex gap-3 tnum">
            <span>{t.columns} cols</span>
            <span>{t.measures} msr</span>
            {data.rels === 0 && <span style={{ color: '#2E7BD0' }}>isolated</span>}
          </div>
          {t.keyColumns.length > 0 && (
            <div className="truncate" style={{ color: 'var(--sem-accent)' }} title={t.keyColumns.join(', ')}>key: {t.keyColumns.join(', ')}</div>
          )}
          {!t.hasDescription && !t.isHidden && <div style={{ color: 'var(--sem-warn)' }}>no description</div>}
        </div>
      ) : (
        <div className="py-1.5">
          <input className="nodrag" value={data.filter} onChange={(e) => data.onFilter(t.name, e.target.value)} placeholder="filter columns…"
            style={{ width: 'calc(100% - 16px)', margin: '0 8px 6px', padding: '2px 6px', fontSize: 10, background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4 }} />
          <div className="nowheel" style={{ maxHeight: 220, overflowY: 'auto', overflowX: 'hidden' }}>
            {cols && cols.primary.map((c) => <ColRow key={c.name} c={c} />)}
            {cols && cols.primary.length > 0 && cols.rest.length > 0 && <div style={{ height: 1, background: 'var(--sem-border)', margin: '3px 8px' }} />}
            {cols && cols.rest.map((c) => <ColRow key={c.name} c={c} />)}
            {cols && cols.primary.length + cols.rest.length === 0 && <div style={{ color: 'var(--sem-muted)', padding: '2px 10px' }}>no matching columns</div>}
          </div>
          <div style={{ color: 'var(--sem-muted)', fontSize: 9, padding: '4px 10px 0' }}>drag a column → another table's column to relate</div>
        </div>
      )}
    </div>
  );
}

// One column row in an expanded table. The ENTIRE row is a connect surface: a single full-row Handle (transparent,
// laid over the row) so dragging anywhere on a column starts a relationship drag — combined with ConnectionMode.Loose,
// you drag one column onto another column and onConnect creates the relationship. The row is `nodrag` so a drag on it
// never moves the whole node (the old failure: the only connect target was a 7px edge nub, so users grabbed the row
// text and the table moved instead). The handle id still encodes the column (col:<name>:<side>) for onConnect. Drag
// the table by its HEADER; drag a column to relate.
function ColRow({ c }: { c: ColumnRow }) {
  return (
    <div className="nodrag relative flex items-center gap-1 sem-colrow" style={{ padding: '2px 10px', fontSize: 10.5, cursor: 'crosshair' }}
      title="Drag this column onto another table's column to create a relationship">
      <Handle type="source" id={`col:${c.name}:right`} position={Position.Right}
        style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', transform: 'none', borderRadius: 0, background: 'transparent', border: 'none', zIndex: 1 }} />
      {c.isKey && <span style={{ color: 'var(--sem-accent)' }}>key</span>}
      <span className="truncate" style={{ color: c.isHidden ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>{c.name}</span>
      <span className="ml-auto shrink-0 text-[9px]" style={{ color: 'var(--sem-muted)' }}>{c.dataType}</span>
      <span className="shrink-0" style={{ color: 'var(--sem-accent)', opacity: 0.55, fontSize: 11, lineHeight: 1, zIndex: 2 }} aria-hidden>⇄</span>
    </div>
  );
}

const tbBtn: React.CSSProperties = {
  background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)',
  borderRadius: 4, fontSize: 10, padding: '2px 6px', cursor: 'pointer',
};
// Active/toggled toolbar button (e.g. snap-to-grid on).
const tbBtnActive: React.CSSProperties = {
  ...tbBtn, background: 'var(--sem-accent-soft)', borderColor: 'var(--sem-accent)', color: 'var(--sem-fg)',
};

const nodeTypes = { table: TableNode };

// dagre positions (top-left coords) for a set of tables + the relationships among them. `sizeOf` gives each node
// its dynamic box so dagre spaces variable-sized nodes without overlap (busy nodes are taller for their Left/Right
// anchors in the LR layout).
function dagrePositions(tables: GraphTable[], rels: GraphRelationship[], sizeOf: SizeOf): Map<string, XY> {
  const names = new Set(tables.map((t) => t.name));
  const g = new dagre.graphlib.Graph();
  g.setDefaultEdgeLabel(() => ({}));
  // Generous separation so relationship lines get their own lanes (dense default layouts were the main source of
  // "lines on top of each other"); ranksep gives long horizontal runs between ranks, nodesep keeps siblings apart.
  g.setGraph({ rankdir: 'LR', nodesep: 60, ranksep: 160, marginx: 28, marginy: 28 });
  tables.forEach((t) => { const s = sizeOf(t.name); g.setNode(t.name, { width: s.w, height: s.h }); });
  rels.forEach((r) => { if (names.has(r.fromTable) && names.has(r.toTable)) g.setEdge(r.fromTable, r.toTable); });
  dagre.layout(g);
  const m = new Map<string, XY>();
  tables.forEach((t) => { const p = g.node(t.name); const s = sizeOf(t.name); m.set(t.name, { x: (p?.x ?? 0) - s.w / 2, y: (p?.y ?? 0) - s.h / 2 }); });
  return m;
}

// Classify each table by its Kimball role from the relationships: a table referenced as a "one" end is a
// DIMENSION, a pure "many" end is a FACT, and one with no relationships is ISOLATED. Also returns each table's
// neighbour set (used to order ranks so related tables line up). Shared by the bus-matrix + layered layouts and
// computeAnchors so handle sides always agree with the placement.
function classifyRoles(tables: GraphTable[], rels: GraphRelationship[]) {
  const names = new Set(tables.map((t) => t.name));
  const isOne = new Set<string>(), isMany = new Set<string>();
  const nbr = new Map<string, Set<string>>();
  const link = (a: string, b: string) => { let s = nbr.get(a); if (!s) nbr.set(a, s = new Set()); s.add(b); };
  for (const r of rels) {
    if (!names.has(r.fromTable) || !names.has(r.toTable)) continue;
    const { manyT, oneT } = roleOf(r);
    isMany.add(manyT); isOne.add(oneT);
    link(r.fromTable, r.toTable); link(r.toTable, r.fromTable);
  }
  const dims: string[] = [], facts: string[] = [], iso: string[] = [];
  for (const t of tables) {
    if (isOne.has(t.name)) dims.push(t.name);          // dimension — referenced by something (incl. snowflake outriggers)
    else if (isMany.has(t.name)) facts.push(t.name);   // pure fact — only ever the many side
    else iso.push(t.name);                             // isolated — no relationships
  }
  return { dims, facts, iso, neighbors: (n: string) => nbr.get(n) ?? new Set<string>() };
}

// Order one rank so each node sits under/over the mean position of its neighbours in the other rank (the classic
// barycenter heuristic) — the single biggest reducer of edge crossings, so relationship lines fan out cleanly
// instead of tangling. Nodes with no placed neighbour fall to the end, in their original order.
function baryOrder(rank: string[], otherIndex: Map<string, number>, neighbors: (n: string) => Set<string>): string[] {
  const key = (n: string) => {
    const ix = [...neighbors(n)].map((m) => otherIndex.get(m)).filter((v): v is number => v != null);
    return ix.length ? ix.reduce((a, b) => a + b, 0) / ix.length : Number.MAX_SAFE_INTEGER;
  };
  return rank.map((n, i) => ({ n, i, k: key(n) })).sort((a, b) => (a.k - b.k) || (a.i - b.i)).map((x) => x.n);
}

// Assign each table a LAYER by its depth from the facts in the many→one DAG: a pure fact = 0, a dimension referenced
// by a layer-L many side sits at L+1, snowflake outriggers (dims referenced by other dims) climb higher. Implemented
// as a bounded relaxation so a cycle (bidi / m2m) can't loop forever.
function layerTables(tables: GraphTable[], rels: GraphRelationship[]): { layer: Map<string, number>; maxL: number } {
  const names = new Set(tables.map((t) => t.name));
  const manyOne: { many: string; one: string }[] = [];
  for (const r of rels) {
    if (!names.has(r.fromTable) || !names.has(r.toTable)) continue;
    const { manyT, oneT } = roleOf(r);
    if (manyT !== oneT) manyOne.push({ many: manyT, one: oneT });
  }
  const layer = new Map(tables.map((t) => [t.name, 0]));
  for (let it = 0; it < tables.length; it++) {
    let changed = false;
    for (const { many, one } of manyOne) {
      const v = (layer.get(many) ?? 0) + 1;
      if (v > (layer.get(one) ?? 0)) { layer.set(one, v); changed = true; }
    }
    if (!changed) break;
  }
  return { layer, maxL: Math.max(0, ...layer.values()) };
}

// Layered = a vertical Sugiyama star: FACTS in the bottom rank, the DIMENSIONS they reference above, snowflake
// OUTRIGGERS above those. Relationships connect ADJACENT ranks only and each rank gets a clear lane, so every line
// is a straight vertical drop through empty space — nothing routed behind a table, no horizontal dim-to-dim tangles.
// Each rank is barycenter-ordered over several sweeps to minimise crossings. Nodes are laid by their DYNAMIC width
// (cumulative within a rank) and each rank is centred on a common axis; busy nodes (many top/bottom anchors) are
// wider, so their per-edge anchors spread into distinct vertical lanes. The clearest view for a star/galaxy schema.
function layeredPositions(tables: GraphTable[], rels: GraphRelationship[], sizeOf: SizeOf): Map<string, XY> {
  const { neighbors } = classifyRoles(tables, rels);
  const connected = tables.filter((t) => neighbors(t.name).size > 0).map((t) => t.name);
  const iso = tables.filter((t) => neighbors(t.name).size === 0).map((t) => t.name);
  const { layer, maxL } = layerTables(tables, rels);
  const ranks: string[][] = [];
  for (let L = 0; L <= maxL; L++) ranks[L] = connected.filter((n) => layer.get(n) === L).sort();
  const idx = (rank: string[]) => new Map(rank.map((n, i) => [n, i]));
  for (let s = 0; s < 4; s++) {   // alternate down/up barycenter sweeps to settle each rank against its neighbours
    if (s % 2 === 0) for (let L = maxL - 1; L >= 0; L--) ranks[L] = baryOrder(ranks[L], idx(ranks[L + 1]), neighbors);
    else for (let L = 1; L <= maxL; L++) ranks[L] = baryOrder(ranks[L], idx(ranks[L - 1]), neighbors);
  }
  const GAP = 64;       // horizontal gap between siblings in a rank
  const LANE = 230;     // vertical lane between ranks (the relationship-line corridor; roomy so the staggered
                        //   center-outward crossbars never crowd — Kane wanted more vertical breathing room)
  const rowW = (r: string[]) => r.reduce((a, n) => a + sizeOf(n).w, 0) + Math.max(0, r.length - 1) * GAP;
  const rowH = (r: string[]) => Math.max(NODE_H, ...r.map((n) => sizeOf(n).h));
  const totalW = Math.max(1, ...ranks.map(rowW));
  // y per rank: top rank (maxL) at y=0, facts (rank 0) end up at the bottom (largest y). Variable row heights.
  const rowY: number[] = []; let y = 0;
  for (let L = maxL; L >= 0; L--) { rowY[L] = y; y += rowH(ranks[L]) + LANE; }
  const m = new Map<string, XY>();
  for (let L = 0; L <= maxL; L++) {
    let x = (totalW - rowW(ranks[L])) / 2;   // centre each rank on the common axis
    for (const n of ranks[L]) { m.set(n, { x, y: rowY[L] }); x += sizeOf(n).w + GAP; }
  }
  // isolated tables in their own row beneath the facts
  const isoY = (rowY[0] ?? 0) + rowH(ranks[0] ?? []) + LANE;
  let ix = 0;
  for (const n of iso) { m.set(n, { x: ix, y: isoY }); ix += sizeOf(n).w + GAP; }
  return m;
}
// Vertical (the "Vertical" button, internally `hierarchy`) = the LAYERED Sugiyama star turned 90° (Kane's "layered
// but rotated"): FACTS in the LEFT rank, the
// DIMENSIONS they reference to the RIGHT, snowflake OUTRIGGERS further right. Ranks run left→right; within a rank
// nodes stack vertically. Identical barycenter ordering + dynamic sizing to layeredPositions, just on swapped axes —
// so every relationship is a horizontal run with a single VERTICAL crossbar (the rotation of the layered crossbar).
function hierarchyPositions(tables: GraphTable[], rels: GraphRelationship[], sizeOf: SizeOf): Map<string, XY> {
  const { neighbors } = classifyRoles(tables, rels);
  const connected = tables.filter((t) => neighbors(t.name).size > 0).map((t) => t.name);
  const iso = tables.filter((t) => neighbors(t.name).size === 0).map((t) => t.name);
  const { layer, maxL } = layerTables(tables, rels);
  const ranks: string[][] = [];
  for (let L = 0; L <= maxL; L++) ranks[L] = connected.filter((n) => layer.get(n) === L).sort();
  const idx = (rank: string[]) => new Map(rank.map((n, i) => [n, i]));
  for (let s = 0; s < 4; s++) {   // alternate barycenter sweeps to settle each rank against its neighbours
    if (s % 2 === 0) for (let L = maxL - 1; L >= 0; L--) ranks[L] = baryOrder(ranks[L], idx(ranks[L + 1]), neighbors);
    else for (let L = 1; L <= maxL; L++) ranks[L] = baryOrder(ranks[L], idx(ranks[L - 1]), neighbors);
  }
  const GAP = 48;       // vertical gap between siblings in a rank
  const LANE = 260;     // horizontal lane between ranks (the relationship-line corridor; roomy for staggered crossbars)
  const colH = (r: string[]) => r.reduce((a, n) => a + sizeOf(n).h, 0) + Math.max(0, r.length - 1) * GAP;
  const colW = (r: string[]) => Math.max(NODE_W, ...r.map((n) => sizeOf(n).w));
  const totalH = Math.max(1, ...ranks.map(colH));
  // x per rank: facts (rank 0) at the LEFT (x=0), dims/outriggers to the right. Variable column widths.
  const colX: number[] = []; let x = 0;
  for (let L = 0; L <= maxL; L++) { colX[L] = x; x += colW(ranks[L]) + LANE; }
  const m = new Map<string, XY>();
  for (let L = 0; L <= maxL; L++) {
    let y = (totalH - colH(ranks[L])) / 2;   // centre each rank on the common (vertical) axis
    for (const n of ranks[L]) { m.set(n, { x: colX[L], y }); y += sizeOf(n).h + GAP; }
  }
  // isolated tables in their own column to the right
  const isoX = (colX[maxL] ?? 0) + colW(ranks[maxL] ?? []) + LANE;
  let iy = 0;
  for (const n of iso) { m.set(n, { x: isoX, y: iy }); iy += sizeOf(n).h + GAP; }
  return m;
}
// Bus matrix (Kimball): DIMENSIONS across the TOP (connecting from their BOTTOM edge), FACTS down the LEFT (joining on
// their RIGHT edge). Facts are barycenter-ordered beside the dims they use; dims hubs-first. Each node is laid by its
// DYNAMIC box (cumulative), so a fact with many relationships is TALLER (its right-edge anchors spread into distinct
// horizontal lanes) and a dim used by many facts is WIDER (its bottom-edge anchors spread) — the fix for "collapsed"
// lines at a busy node. The lines are orthogonal L-routes (fact-right → up → dim-bottom).
function busMatrixPositions(tables: GraphTable[], rels: GraphRelationship[], sizeOf: SizeOf): Map<string, XY> {
  const { dims, facts, iso, neighbors } = classifyRoles(tables, rels);
  const GAP = 72;
  const dimOrder = [...dims].sort((a, b) => neighbors(b).size - neighbors(a).size);
  const factOrder = baryOrder(facts, new Map(dimOrder.map((n, i) => [n, i])), neighbors);
  // The fact column / dim row must each clear the other so the L-routes never run behind a table.
  const factColW = Math.max(NODE_W, ...factOrder.map((n) => sizeOf(n).w));
  const dimRowH = Math.max(NODE_H, ...dimOrder.map((n) => sizeOf(n).h));
  const dimStartX = factColW + GAP * 1.5;
  const factStartY = dimRowH + GAP * 1.5;
  const m = new Map<string, XY>();
  let x = dimStartX;
  for (const n of dimOrder) { m.set(n, { x, y: 0 }); x += sizeOf(n).w + GAP; }   // dims across the top
  let y = factStartY;
  for (const n of factOrder) { m.set(n, { x: 0, y }); y += sizeOf(n).h + GAP; }  // facts down the left
  const rightX = x + 60;
  let iy = 0;
  for (const n of iso) { m.set(n, { x: rightX, y: iy }); iy += sizeOf(n).h + GAP; }   // isolated on the far right
  return m;
}

// Resolve positions for the shown tables: keep any KNOWN position (a prior drag or saved layout),
// and only place the UNKNOWN ones — full dagre when the diagram is fresh, else stacked to the right
// of the existing layout so adding tables never disturbs what you've already arranged.
function placePositions(membership: GraphTable[], rels: GraphRelationship[], known: Record<string, XY>, sizeOf: SizeOf): Map<string, XY> {
  if (!membership.some((t) => known[t.name])) return dagrePositions(membership, rels, sizeOf);
  const result = new Map<string, XY>();
  const knownT = membership.filter((t) => known[t.name]);
  const unknownT = membership.filter((t) => !known[t.name]);
  knownT.forEach((t) => result.set(t.name, known[t.name]));
  const startX = Math.max(...knownT.map((t) => known[t.name].x + sizeOf(t.name).w)) + 80;
  const startY = Math.min(...knownT.map((t) => known[t.name].y));
  let y = startY;
  unknownT.forEach((t) => { result.set(t.name, { x: startX, y }); y += sizeOf(t.name).h + 28; });
  return result;
}

// Many/One role of a relationship's two ends — mirrors busMatrixPositions's classification so handle sides agree.
function roleOf(r: GraphRelationship): { manyT: string; oneT: string } {
  if (r.fromCardinality === 'Many') return { manyT: r.fromTable, oneT: r.toTable };
  if (r.toCardinality === 'Many') return { manyT: r.toTable, oneT: r.fromTable };
  return { manyT: r.fromTable, oneT: r.toTable };
}

// Per-table relationship anchor slots. Each relationship end gets a DISTINCT point on a node side (frac), so two
// edges never enter/exit at the same midpoint. In bus-matrix the one-side (dimension) end drops from the BOTTOM and
// the many-side end leaves the TOP (dims sit above facts); otherwise source=Right / target=Left (the LR layout).
// Order an edge's anchors along its side by the DIRECTION to the connected table — oriented so the key increases
// with the side's frac (Top/Bottom run left→right; Left/Right run top→bottom). This makes a fan of lines stay
// crossing-free and the slot order mirror the connected tables' on-screen order. Verified to give the bus-matrix
// rule: the LEFTMOST dim attaches at the TOP of a fact's right edge, and the TOPMOST fact at the LEFT of a dim's
// bottom edge (and the mirror at every other side). Position-driven, so it re-sorts when a table moves.
function anchorKey(side: Position, centers: Map<string, XY>, table: string, other: string): number {
  const a = centers.get(table), b = centers.get(other);
  if (!a || !b) return 0;
  const dx = b.x - a.x, dy = b.y - a.y;
  if (side === Position.Top) return Math.atan2(dx, -dy);     // left → right along the top edge
  if (side === Position.Bottom) return Math.atan2(dx, dy);   // left → right along the bottom edge
  if (side === Position.Right) return Math.atan2(dy, dx);    // top → bottom down the right edge
  return Math.atan2(dy, -dx);                                // top → bottom down the left edge
}

function computeAnchors(members: GraphTable[], rels: GraphRelationship[], layout: LayoutMode, centers?: Map<string, XY>): Map<string, Anchor[]> {
  const names = new Set(members.map((t) => t.name));
  const ends: { table: string; other: string; rel: string; kind: 'source' | 'target'; side: Position }[] = [];
  for (const r of rels) {
    if (!names.has(r.fromTable) || !names.has(r.toTable)) continue;
    let fromSide: Position, toSide: Position;
    if (layout === 'layered') {
      // Vertical ranks (facts below, dims above): the one-side (dimension) faces DOWN toward the facts, the many-side
      // (fact) faces UP toward the dims — clean vertical lanes between adjacent ranks.
      const { oneT } = roleOf(r);
      fromSide = r.fromTable === oneT ? Position.Bottom : Position.Top;
      toSide = r.toTable === oneT ? Position.Bottom : Position.Top;
    } else if (layout === 'busmatrix') {
      // Bus matrix (dims top, facts left): the fact (many-side) joins on its RIGHT, the dimension (one-side) connects
      // from its BOTTOM — an orthogonal L-route between the left fact column and the top dim row.
      const { oneT } = roleOf(r);
      fromSide = r.fromTable === oneT ? Position.Bottom : Position.Right;
      toSide = r.toTable === oneT ? Position.Bottom : Position.Right;
    } else if (layout === 'hierarchy') {
      // Vertical (layered rotated 90°: facts left, dims right): the many-side (fact) leaves its RIGHT, the
      // one-side (dimension) connects on its LEFT — horizontal lanes with a single vertical crossbar (mirrors layered).
      const { oneT } = roleOf(r);
      fromSide = r.fromTable === oneT ? Position.Left : Position.Right;
      toSide = r.toTable === oneT ? Position.Left : Position.Right;
    } else {
      fromSide = Position.Right; toSide = Position.Left;
    }
    ends.push({ table: r.fromTable, other: r.toTable, rel: r.name, kind: 'source', side: fromSide });
    ends.push({ table: r.toTable, other: r.fromTable, rel: r.name, kind: 'target', side: toSide });
  }
  const groups = new Map<string, typeof ends>();
  for (const e of ends) { const k = e.table + '|' + e.side; const g = groups.get(k); if (g) g.push(e); else groups.set(k, [e]); }
  const out = new Map<string, Anchor[]>();
  for (const g of groups.values()) {
    // Sort the ends sharing this edge by the angle to their connected table (when positions are known), so the
    // attach points run sequentially along the edge instead of in arbitrary relationship order.
    if (centers) g.sort((a, b) => anchorKey(a.side, centers, a.table, a.other) - anchorKey(b.side, centers, b.table, b.other));
    g.forEach((e, i) => {
      const a: Anchor = { rel: e.rel, kind: e.kind, side: e.side, frac: (i + 1) / (g.length + 1) };
      const arr = out.get(e.table); if (arr) arr.push(a); else out.set(e.table, [a]);
    });
  }
  return out;
}

// Each relationship END's ORDER (0-based) along the node side it sits on + that fan's size, derived from the
// POSITION-sorted anchors. The elbow offset uses this so a fan's lines turn on distinct, SEQUENTIAL lanes — e.g. in
// the layered view each dim's vertical drop length grows with its sequence, staggering the horizontal segments so
// they never collapse onto one line. (Keyed table|rel|kind to match the source/target end of an edge.)
function anchorSeqIndex(anchors: Map<string, Anchor[]>): Map<string, { i: number; n: number }> {
  const out = new Map<string, { i: number; n: number }>();
  for (const [table, arr] of anchors) {
    const bySide = new Map<Position, Anchor[]>();
    for (const a of arr) { const g = bySide.get(a.side); if (g) g.push(a); else bySide.set(a.side, [a]); }
    for (const g of bySide.values()) {
      g.sort((x, y) => x.frac - y.frac);   // along-the-edge order (already the angle-sorted order)
      g.forEach((a, i) => out.set(table + '|' + a.rel + '|' + a.kind, { i, n: g.length }));
    }
  }
  return out;
}

// The single crossbar HEIGHT for each VERTICAL (layered) edge — varied from the fact-aligned centre OUTWARD and
// SYMMETRIC. Within a fact's fan, the dims on each side are ranked by their horizontal distance from the fact's
// vertical axis: the nearest dim's crossbar sits closest to the dim row (small lane), and each step OUTWARD moves the
// crossbar toward the fact (larger lane). A left dim and its mirror-right dim share a rank → the same height (they
// approach the fact from opposite sides, so they don't overlap). Each fact's fan also gets a per-fact BAND offset so
// two facts sharing a conformed dim never place that dim's two crossbars at the same level. lane ∈ (0,1): 0 = at the
// dim (target), 1 = at the fact (source). Only consumed for layered (vertical) edges; other layouts ignore it.
function computeLanes(rels: GraphRelationship[], anchors: Map<string, Anchor[]>, centers: Map<string, XY>): Map<string, number> {
  const out = new Map<string, number>();
  const sideOf = (table: string, rel: string, kind: 'source' | 'target') =>
    (anchors.get(table) ?? []).find((a) => a.rel === rel && a.kind === kind)?.side;
  // A fan hub = a relationship's SOURCE end (the many-side/fact) on one edge. A VERTICAL hub (Top/Bottom, layered) →
  // horizontal crossbar staggered in Y; a HORIZONTAL hub (Left/Right, rotated/auto-arrange) → vertical crossbar
  // staggered in X. Same centre-outward, per-fact-band math either way, just on the perpendicular axis.
  const fans = new Map<string, { rels: GraphRelationship[]; horiz: boolean }>();
  for (const r of rels) {
    const s = sideOf(r.fromTable, r.name, 'source');
    const vert = s === Position.Top || s === Position.Bottom;
    const horiz = s === Position.Left || s === Position.Right;
    if (!vert && !horiz) continue;
    let f = fans.get(r.fromTable); if (!f) fans.set(r.fromTable, f = { rels: [], horiz });
    f.rels.push(r);
  }
  // Order facts along the cross-axis (the axis the crossbars stagger on) for the per-fact BAND offset.
  const factOrder = [...fans.entries()].sort((a, b) => {
    const ca = centers.get(a[0]), cb = centers.get(b[0]);
    return a[1].horiz ? (ca?.y ?? 0) - (cb?.y ?? 0) : (ca?.x ?? 0) - (cb?.x ?? 0);
  });
  factOrder.forEach(([fact, f], fi) => {
    const fc = centers.get(fact);
    if (!fc) { f.rels.forEach((r) => out.set(r.name, 0.5)); return; }
    const band = (fi % 2) * 0.08;                       // alternate facts into offset bands → never the same level
    // Perpendicular distance of each dim from the fact's axis (X for vertical hubs, Y for horizontal/rotated hubs).
    const dist = (r: GraphRelationship) => {
      const tc = centers.get(r.toTable);
      return f.horiz ? (tc?.y ?? fc.y) - fc.y : (tc?.x ?? fc.x) - fc.x;
    };
    const lane = (rank: number) => Math.min(0.9, 0.12 + band + rank * 0.16);   // centre → near dim; outward → toward fact
    f.rels.filter((r) => dist(r) <= 0).sort((a, b) => Math.abs(dist(a)) - Math.abs(dist(b))).forEach((r, rank) => out.set(r.name, lane(rank)));
    f.rels.filter((r) => dist(r) > 0).sort((a, b) => Math.abs(dist(a)) - Math.abs(dist(b))).forEach((r, rank) => out.set(r.name, lane(rank)));
  });
  return out;
}

function edgesFor(rels: GraphRelationship[], memberNames: Set<string>, anchors: Map<string, Anchor[]>, centers: Map<string, XY>): Edge[] {
  // Two staggers, both position-driven: `offset` separates non-vertical fans (a busy node's lines turn on distinct
  // lanes, not one collapsed midpoint), and `lane` (computeLanes) sets the single crossbar height for vertical/layered
  // edges from the fact-aligned centre outward. Both follow the on-screen fan, not relationship order.
  const seq = anchorSeqIndex(anchors);
  const laneOf = computeLanes(rels, anchors, centers);
  return rels
    .filter((r) => memberNames.has(r.fromTable) && memberNames.has(r.toTable))
    .map((r, i) => {
      const bidi = r.crossFilter === 'BothDirections';
      const tint: Tint = !r.isActive ? 'muted' : bidi ? 'warn' : 'accent';
      const color = `var(--sem-${tint})`;
      // Cardinality is shown by crow's-foot markers drawn in SemRelEdge (fork=many, bar=one), rotated to the line.
      const id = r.name || `rel-${i}`;
      const sSeq = seq.get(r.fromTable + '|' + r.name + '|source') ?? { i: 0, n: 1 };
      const tSeq = seq.get(r.toTable + '|' + r.name + '|target') ?? { i: 0, n: 1 };
      return {
        id,
        source: r.fromTable, target: r.toTable,
        // Handle ids use r.name so they always match computeAnchors's anchor ids (which also key on r.name).
        sourceHandle: r.name + ':source', targetHandle: r.name + ':target',
        type: 'semrel', animated: bidi && r.isActive,
        // lane ∈ (0,1): the single crossbar's height for a vertical (layered) edge (computeLanes, centre→outward).
        // offset: the per-edge lane stagger for non-vertical edges. fromMany/toMany pick each end's marker (fork/bar).
        data: { lane: laneOf.get(r.name) ?? 0.5, offset: 12 + sSeq.i * 10 + tSeq.i * 6, fromMany: r.fromCardinality === 'Many', toMany: r.toCardinality === 'Many', title: `${r.fromTable}[${r.fromColumn}] (${uiLabel(r.fromCardinality)}) → ${r.toTable}[${r.toColumn}] (${uiLabel(r.toCardinality)}) · ${uiLabel(r.crossFilter)}${r.isActive ? '' : ' · inactive'}` },
        style: { stroke: color, strokeWidth: 1.5, strokeDasharray: r.isActive ? undefined : '5 4' },
      } as Edge;
    });
}

// Crow's-foot cardinality markers ("Many" → fork, "One" → bar), rendered once into the document; React Flow's
// edges reference them by id. Separate start/end variants so each fork opens toward its entity regardless of path
// direction, and one variant per line state (tint) so the marker colour matches the relationship's colour. Stroke
// is set via inline style (a CSS var() in a presentation attribute isn't resolved by Chromium).
type Tint = 'accent' | 'warn' | 'muted';
const MARKER_SHAPES: { id: string; refX: number; d: string }[] = [
  { id: 'many-end', refX: 11, d: 'M1,6 L11,1 M1,6 L11,11' },
  { id: 'one-end', refX: 11, d: 'M8,1.5 L8,10.5' },
  { id: 'many-start', refX: 1, d: 'M11,6 L1,1 M11,6 L1,11' },
  { id: 'one-start', refX: 1, d: 'M4,1.5 L4,10.5' },
];
const TINTS: Tint[] = ['accent', 'warn', 'muted'];
function CardinalityMarkers() {
  return (
    <svg style={{ position: 'absolute', width: 0, height: 0 }} aria-hidden>
      <defs>
        {TINTS.flatMap((tint) => MARKER_SHAPES.map((s) => (
          <marker key={`${s.id}-${tint}`} id={`sem-${s.id}-${tint}`} viewBox="0 0 12 12" markerWidth="17" markerHeight="17" refX={s.refX} refY="6" orient="auto" markerUnits="userSpaceOnUse">
            <path d={s.d} style={{ stroke: `var(--sem-${tint})`, strokeWidth: 1.4, fill: 'none' }} />
          </marker>
        )))}
      </defs>
    </svg>
  );
}

const relCounts = (rels: GraphRelationship[]): Map<string, number> => {
  const m = new Map<string, number>();
  for (const r of rels) { m.set(r.fromTable, (m.get(r.fromTable) ?? 0) + 1); m.set(r.toTable, (m.get(r.toTable) ?? 0) + 1); }
  return m;
};
const nodesToPositions = (nodes: Node[]): Record<string, XY> => {
  const p: Record<string, XY> = {};
  for (const n of nodes) p[n.id] = { x: n.position.x, y: n.position.y };
  return p;
};
// Engine layout (LayoutNode[], keyed by table name) → the canvas's name-keyed position map. The engine owns the
// "All tables" layout (.semanticus/layout.json, LineageTag-keyed, survives reloads + cross-machine + the agent
// moving tables); custom diagrams stay client-side. The webview saves with origin 'human' so it ignores its echo.
const ENGINE_LAYOUT_ORIGIN = 'human';
const layoutToPositions = (tables: { name: string; x: number; y: number }[] | undefined): Record<string, XY> => {
  const p: Record<string, XY> = {};
  for (const t of tables ?? []) if (t.name) p[t.name] = { x: t.x, y: t.y };
  return p;
};
const positionsToLayoutNodes = (pos: Record<string, XY>) =>
  Object.entries(pos).map(([name, p]) => ({ name, x: p.x, y: p.y }));

// Highest add-request nonce already consumed — module-level so it survives DiagramInner remounts (the canvas
// unmounts when you leave the tab), preventing a stale request from re-firing (and re-creating a diagram) on return.
let consumedAddNonce = 0;

function DiagramInner({ addReq }: { addReq: { tables: string[]; nonce: number } | null }) {
  const [graph, setGraph] = useState<ModelGraph | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [diagrams, setDiagrams] = useState<SavedDiagram[]>(() => loadState<SavedDiagram[]>('diagrams', [DEFAULT_DIAGRAM]));
  const [activeId, setActiveId] = useState<string>(() => loadState<string>('activeDiagramId', ALL_ID));
  const [renaming, setRenaming] = useState(false);
  const [nameDraft, setNameDraft] = useState('');
  const [snap, setSnap] = useState<boolean>(() => loadState<boolean>('diagramSnap', false));   // snap-to-grid toggle
  const [columns, setColumns] = useState<ColumnRow[] | null>(null);
  const [selectedRel, setSelectedRel] = useState<string | null>(null);   // edge id (= relationship name) for the props panel
  const [focusedEdge, setFocusedEdge] = useState<string | null>(null);   // single-click: highlight one relationship, fade the rest
  const [toast, setToast] = useState<Toast | null>(null);                // create-relationship feedback
  const [filters, setFilters] = useState<Record<string, string>>({});    // per-table column search (transient)

  const [nodes, setNodes, onNodesChange] = useNodesState<Node<GraphCardData>>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);
  const rf = useReactFlow();
  const updateNodeInternals = useUpdateNodeInternals();

  const active = useMemo(() => diagrams.find((d) => d.id === activeId) ?? diagrams[0] ?? DEFAULT_DIAGRAM, [diagrams, activeId]);
  // Tracks whether the All-tables diagram is active, readable from the once-mounted layout/didChange handler
  // without re-subscribing (avoids a stale closure on `active.all`).
  const allActiveRef = useRef(active.all);
  useEffect(() => { allActiveRef.current = active.all; }, [active.all]);

  // The tables this diagram SCOPES to, in model order (curated set ∩ still-existing tables) — before type filters.
  const baseMembers = useMemo<GraphTable[]>(() => {
    if (!graph) return [];
    if (active.all) return graph.tables;
    const set = new Set(active.tables);
    return graph.tables.filter((t) => set.has(t.name));
  }, [graph, active]);
  // Type-kind toggles — hide whole table KINDS from the canvas. Persisted like the other diagram view state (snap),
  // all ON by default. Each table is exactly one kind (kindOf), so toggling Calculated off never hides field params.
  const [showData, setShowData] = useState<boolean>(() => loadState<boolean>('diagramKindData', true));
  const [showCalc, setShowCalc] = useState<boolean>(() => loadState<boolean>('diagramKindCalc', true));
  const [showFp, setShowFp] = useState<boolean>(() => loadState<boolean>('diagramKindFp', true));
  const shownKind = useCallback((t: GraphTable) => { const k = kindOf(t); return k === 'fp' ? showFp : k === 'calc' ? showCalc : showData; }, [showData, showCalc, showFp]);
  // The tables actually RENDERED = scope minus any hidden kinds. Edges to a hidden table drop out automatically
  // (edgesFor filters relationships to the member set), so filtering never leaves a dangling relationship line.
  const members = useMemo<GraphTable[]>(() => baseMembers.filter(shownKind), [baseMembers, shownKind]);
  // Per-kind counts (from the scope, not the filtered set) so each chip shows how many it governs.
  const kindCounts = useMemo(() => {
    let data = 0, calc = 0, fp = 0;
    for (const t of baseMembers) { const k = kindOf(t); if (k === 'fp') fp++; else if (k === 'calc') calc++; else data++; }
    return { data, calc, fp };
  }, [baseMembers]);
  // An explicit add (drop / picker / host request) is an unambiguous "show me this table" — if its kind is
  // currently filtered out, flip that kind's toggle back on so the add never lands invisibly.
  const revealKinds = useCallback((names: string[]) => {
    if (!graph) return;
    const byName = new Map(graph.tables.map((t) => [t.name, t]));
    for (const n of names) {
      const t = byName.get(n); if (!t) continue;
      const k = kindOf(t);
      if (k === 'fp') setShowFp(true); else if (k === 'calc') setShowCalc(true); else setShowData(true);
    }
  }, [graph]);
  // Stable identity of the shown set — drives a relayout when membership OR the kind filters change (NOT on drag).
  const membershipKey = (active.all ? 'ALL' : members.map((t) => t.name).join('|')) + `#${showData ? 1 : 0}${showCalc ? 1 : 0}${showFp ? 1 : 0}`;
  const layout: LayoutMode = active.layout ?? 'free';
  const expandedKey = JSON.stringify(active.expanded ?? {});

  // Column data for the expand-to-connect feature + the relationship-direction heuristic.
  const colsByTable = useMemo(() => {
    const m = new Map<string, ColumnRow[]>();
    for (const c of columns ?? []) { const a = m.get(c.table); if (a) a.push(c); else m.set(c.table, [c]); }
    return m;
  }, [columns]);
  const colByRef = useMemo(() => new Map((columns ?? []).map((c) => [c.ref, c])), [columns]);
  // Column names already in a relationship, per table — sorted to the top of an expanded table (Kane's "related first").
  const relatedByTable = useMemo(() => {
    const m = new Map<string, Set<string>>();
    const add = (t: string, c: string) => { const s = m.get(t); if (s) s.add(c); else m.set(t, new Set([c])); };
    for (const r of graph?.relationships ?? []) { add(r.fromTable, r.fromColumn); add(r.toTable, r.toColumn); }
    return m;
  }, [graph]);

  // persist
  useEffect(() => { saveState('diagrams', diagrams); }, [diagrams]);
  useEffect(() => { saveState('activeDiagramId', activeId); }, [activeId]);
  useEffect(() => { saveState('diagramSnap', snap); }, [snap]);
  useEffect(() => { saveState('diagramKindData', showData); }, [showData]);
  useEffect(() => { saveState('diagramKindCalc', showCalc); }, [showCalc]);
  useEffect(() => { saveState('diagramKindFp', showFp); }, [showFp]);
  // heal a stale active id (e.g. a custom diagram deleted in a prior session) so the switcher never dangles
  useEffect(() => { if (!diagrams.some((d) => d.id === activeId)) setActiveId(ALL_ID); }, [diagrams, activeId]);

  async function load() {
    try {
      // Engine-owned layout is the source of truth for the All-tables diagram. Fetch it alongside the graph and
      // SEED the 'all' diagram BEFORE setGraph, so the graph-triggered relayout reads the engine positions.
      const [g, cols, lay] = await Promise.all([
        rpc<ModelGraph>('getModelGraph'),
        rpc<ColumnRow[]>('listColumns'),
        rpc<LayoutData>('getLayout').catch(() => ({ tables: [] } as LayoutData)),   // best-effort; canvas works without it
      ]);
      const pos = layoutToPositions(lay?.tables);
      if (Object.keys(pos).length)
        setDiagrams((ds) => ds.map((d) => (d.id === ALL_ID ? { ...d, positions: { ...d.positions, ...pos } } : d)));
      setGraph(g); setColumns(cols); setErr(null);
    } catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  useEffect(() => {
    void load();
    let timer: number | undefined;
    const off = onDidChange((n) => {
      if (n.deltas?.some((d) => d.kind === 'structure' || d.kind === 'add' || d.kind === 'remove') ?? true) {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => void load(), 300);
      }
    });
    // Live cross-door sync: when the OTHER door (the user's Claude / a second client) saves layout, move the
    // All-tables diagram. Ignore our own echo (we save as 'human'). Always update the stored 'all' positions; move
    // the live nodes only while the All-tables diagram is shown (a custom diagram's layout is client-owned).
    const offLayout = onLayoutChange((v) => {
      if (v.origin === ENGINE_LAYOUT_ORIGIN) return;
      const pos = layoutToPositions(v.tables);
      if (!Object.keys(pos).length) return;
      setDiagrams((ds) => ds.map((d) => (d.id === ALL_ID ? { ...d, positions: { ...d.positions, ...pos } } : d)));
      if (allActiveRef.current) setNodes((nds) => nds.map((n) => (pos[n.id] ? { ...n, position: pos[n.id] } : n)));
    });
    return () => { off(); offLayout(); window.clearTimeout(timer); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // --- mutators (curated diagrams) ---
  const editActive = useCallback((fn: (d: SavedDiagram) => SavedDiagram) =>
    setDiagrams((ds) => ds.map((d) => (d.id === activeId ? fn(d) : d))), [activeId]);

  const addTables = useCallback((names: string[]) => {
    revealKinds(names);   // every add path (picker, palette, ＋ related) must surface the table, not hide it behind a filter
    editActive((d) => ({ ...d, all: false, tables: uniq([...d.tables, ...names]) }));
  }, [editActive, revealKinds]);
  // Add tables at a dropped canvas position (drag from the Model tree). Each NEW table is seeded a position near the
  // drop point (cascaded so a multi-select doesn't stack exactly), so the reconcile places it there instead of dagre-ing.
  const addTablesAt = useCallback((names: string[], at: XY) => {
    revealKinds(names);
    editActive((d) => {
      const positions = { ...d.positions };
      names.forEach((n, i) => { if (!d.tables.includes(n)) positions[n] = { x: at.x + i * 28, y: at.y + i * 28 }; });
      return { ...d, all: false, tables: uniq([...d.tables, ...names]), positions };
    });
  }, [editActive, revealKinds]);
  const removeTable = useCallback((name: string) =>
    editActive((d) => { const { [name]: _drop, ...rest } = d.positions; return { ...d, tables: d.tables.filter((t) => t !== name), positions: rest }; }), [editActive]);
  const addRelated = useCallback((name: string) => {
    if (!graph) return;
    const neighbors = graph.relationships.flatMap((r) => r.fromTable === name ? [r.toTable] : r.toTable === name ? [r.fromTable] : []);
    if (neighbors.length) addTables(neighbors);
  }, [graph, addTables]);

  // --- expand-to-connect ---
  const onToggleExpand = useCallback((name: string) =>
    editActive((d) => ({ ...d, expanded: { ...(d.expanded ?? {}), [name]: !(d.expanded ?? {})[name] } })), [editActive]);
  const onFilter = useCallback((name: string, q: string) => setFilters((f) => ({ ...f, [name]: q })), []);
  const setExpandAll = useCallback((on: boolean) =>
    editActive((d) => ({ ...d, expanded: on ? Object.fromEntries(members.map((t) => [t.name, true])) : {} })), [editActive, members]);

  // Reconcile the canvas whenever the graph reloads, the diagram switches, or membership changes.
  // Keeps live (un-saved) drag positions on a graph reload of the SAME diagram; uses the diagram's
  // saved positions on a switch. New tables are placed without disturbing the existing layout.
  const prevActive = useRef(activeId);
  useEffect(() => {
    if (!graph) return;
    const switched = prevActive.current !== activeId;
    prevActive.current = activeId;
    const counts = relCounts(graph.relationships);
    const memberNames = new Set(members.map((t) => t.name));
    const expanded = active.expanded ?? {};
    // Current positions: keep live drags on a same-diagram reload; a switch resets to the saved layout. Computed
    // outside setNodes so the SAME ordered anchors drive the nodes' handles AND the edges' elbow lanes (edgesFor).
    const live = switched ? new Map<string, XY>() : new Map(rf.getNodes().map((n) => [n.id, n.position as XY]));
    const known: Record<string, XY> = {};
    for (const t of members) { const p = live.get(t.name) ?? active.positions[t.name]; if (p) known[t.name] = p; }

    // The All-tables diagram, until it's explicitly arranged, opens in the DEFAULT arrangement — BUS MATRIX (Kane's
    // default-on-open). We ARRANGE (not just relabel) so the anchor SIDES match the geometry, overriding any native
    // PBIP/Desktop free-form positions (which stay on disk, untouched); the mode is then persisted so a later drag
    // keeps bus-matrix sides. Custom diagrams keep their own composed layout (free) until the user picks one.
    const arranging = !active.layout && active.all;
    const effLayout: LayoutMode = active.layout ?? 'busmatrix';
    // Sides + per-side COUNTS are position-independent (they set node size + the layout's footprint); the per-edge
    // slot ORDER is then computed below from the placed positions (the angle sort), so a fan attaches sequentially.
    const sides = computeAnchors(members, graph.relationships, effLayout);
    const sizeOf: SizeOf = (n) => sizeForAnchors(sides.get(n) ?? EMPTY_ANCHORS);
    const placed = arranging
      ? busMatrixPositions(members, graph.relationships, sizeOf)        // fresh All-tables → default bus matrix
      : placePositions(members, graph.relationships, known, sizeOf);    // else keep saved/dragged positions
    // Order each table's anchor slots by the now-known positions (centre = position + half size) — the angle sort.
    const centers = new Map<string, XY>();
    for (const t of members) { const p = placed.get(t.name); if (p) { const s = sizeOf(t.name); centers.set(t.name, { x: p.x + s.w / 2, y: p.y + s.h / 2 }); } }
    const anchors = computeAnchors(members, graph.relationships, effLayout, centers);
    setNodes(members.map((t) => {
      const sz = sizeOf(t.name);
      return {
        id: t.name, type: 'table',
        position: placed.get(t.name) ?? { x: 0, y: 0 },
        data: {
          t, rels: counts.get(t.name) ?? 0, curated: !active.all,
          anchors: anchors.get(t.name) ?? EMPTY_ANCHORS, w: sz.w, h: sz.h,
          expanded: !!expanded[t.name],
          columns: expanded[t.name] ? (colsByTable.get(t.name) ?? []) : null,
          related: relatedByTable.get(t.name) ?? EMPTY_SET,
          filter: filters[t.name] ?? '',
          onAddRelated: addRelated, onRemove: removeTable, onToggleExpand, onFilter,
        },
      };
    }));
    setEdges(edgesFor(graph.relationships, memberNames, anchors, centers));
    // Persist the default arrangement once so it sticks (a later drag then keeps bus-matrix anchor sides, not free),
    // and frame it (the mount-time fitView fires before the async graph load, so the fresh arrange needs its own fit).
    if (arranging) {
      editActive((d) => ({ ...d, layout: effLayout, positions: { ...d.positions, ...Object.fromEntries(placed) } }));
      window.setTimeout(() => rf.fitView({ padding: 0.2 }), 60);
    }
    // anchors re-sided / columns shown-or-hidden change a node's handle geometry → tell React Flow to re-measure
    window.setTimeout(() => members.forEach((t) => updateNodeInternals(t.name)), 0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [graph, activeId, membershipKey, layout, expandedKey, columns]);

  // A per-node column filter affects ONLY that node's data — patch the single node instead of rebuilding all nodes
  // (which would churn every table + interrupt a hover/drag on each keystroke).
  useEffect(() => {
    setNodes((nds) => nds.map((n) => { const f = filters[n.id] ?? ''; return n.data.filter === f ? n : { ...n, data: { ...n.data, filter: f } }; }));
  }, [filters, setNodes]);

  // Persist the All-tables positions to the engine sidecar (fire-and-forget, best-effort). Custom diagrams stay
  // client-side; only the All-tables layout is engine-owned (the shared, cross-machine, agent-visible one).
  const saveEngineLayout = useCallback((pos: Record<string, XY>) => {
    void rpc('saveLayout', positionsToLayoutNodes(pos), ENGINE_LAYOUT_ORIGIN).catch(() => { /* best-effort */ });
  }, []);

  // Re-sort every table's anchor slots + the edge elbow lanes from the CURRENT node positions (the angle sort), so
  // dragging a table keeps the fans sequential and crossing-free. Patches node DATA only (position is React Flow's).
  const restyleFromPositions = useCallback(() => {
    if (!graph) return;
    const sides = computeAnchors(members, graph.relationships, layout);
    const sizeOf: SizeOf = (n) => sizeForAnchors(sides.get(n) ?? EMPTY_ANCHORS);
    const centers = new Map<string, XY>();
    for (const n of rf.getNodes()) { const s = sizeOf(n.id); centers.set(n.id, { x: n.position.x + s.w / 2, y: n.position.y + s.h / 2 }); }
    const anchors = computeAnchors(members, graph.relationships, layout, centers);
    setNodes((ns) => ns.map((n) => ({ ...n, data: { ...n.data, anchors: anchors.get(n.id) ?? EMPTY_ANCHORS } })));
    setEdges(edgesFor(graph.relationships, new Set(members.map((t) => t.name)), anchors, centers));
    members.forEach((t) => updateNodeInternals(t.name));
  }, [graph, members, layout, rf, setNodes, setEdges, updateNodeInternals]);

  // Live (position-driven) re-sort while dragging — rAF-throttled to at most one recompute per frame so the attach
  // points + lanes follow the table you're moving without thrashing on every drag event.
  const dragRaf = useRef(0);
  const onNodeDrag = useCallback(() => {
    if (dragRaf.current) return;
    dragRaf.current = requestAnimationFrame(() => { dragRaf.current = 0; restyleFromPositions(); });
  }, [restyleFromPositions]);

  // commit current node positions into the active diagram (so they survive a reload) + the engine (for All-tables)
  const commitPositions = useCallback(() => {
    if (dragRaf.current) { cancelAnimationFrame(dragRaf.current); dragRaf.current = 0; }
    restyleFromPositions();   // final crisp re-sort on drop
    const pos = nodesToPositions(rf.getNodes());
    editActive((d) => ({ ...d, positions: { ...d.positions, ...pos } }));
    if (active.all) saveEngineLayout(pos);
  }, [rf, editActive, active.all, saveEngineLayout, restyleFromPositions]);

  // Apply a layout function to the shown tables, record the layout mode (so handle sides re-side), collapse all
  // (the fixed-NODE_H dagre/matrix math assumes collapsed nodes), persist positions, and fit the view.
  const arrangeWith = useCallback((layoutFn: (m: GraphTable[], r: GraphRelationship[], sizeOf: SizeOf) => Map<string, XY>, mode: LayoutMode) => {
    if (!graph) return;
    // Arrange the FULL diagram scope (baseMembers), never the kind-filtered view: the filters hide tables, they
    // don't evict them — so an arrangement must keep placing (and persisting) hidden tables too. Arranging only
    // the visible subset then writing the whole map would silently drop the hidden tables' saved positions (and
    // wipe the map entirely with every kind hidden); re-enabling a kind now lands those tables in their coherent
    // slots of the SAME arrangement instead of stacked outside it as unknowns.
    // Size each node from the anchors of the TARGET layout (handle sides differ per layout), so spacing matches
    // exactly the box the node will render at — busy nodes get their own wider/taller footprint.
    const anchors = computeAnchors(baseMembers, graph.relationships, mode);
    const sizeOf: SizeOf = (n) => sizeForAnchors(anchors.get(n) ?? EMPTY_ANCHORS);
    const placed = layoutFn(baseMembers, graph.relationships, sizeOf);
    setNodes((ns) => ns.map((n) => ({ ...n, position: placed.get(n.id) ?? n.position })));
    editActive((d) => ({ ...d, positions: Object.fromEntries(placed), layout: mode, expanded: {} }));
    if (active.all) saveEngineLayout(Object.fromEntries(placed));
    window.setTimeout(() => rf.fitView({ padding: 0.2, duration: 300 }), 30);
  }, [graph, baseMembers, setNodes, editActive, rf, active.all, saveEngineLayout]);
  const autoArrange = useCallback(() => arrangeWith(hierarchyPositions, 'hierarchy'), [arrangeWith]);
  const busMatrix = useCallback(() => arrangeWith(busMatrixPositions, 'busmatrix'), [arrangeWith]);
  const layered = useCallback(() => arrangeWith(layeredPositions, 'layered'), [arrangeWith]);

  // --- drop tables onto the canvas ---
  // preventDefault on dragover is what makes the drop fire (do NOT pin dropEffect — that can make Chromium cancel it).
  const onCanvasDragOver = useCallback((e: React.DragEvent) => { e.preventDefault(); }, []);
  const onCanvasDrop = useCallback(async (e: React.DragEvent) => {
    e.preventDefault();
    const { clientX, clientY } = e;   // capture before any await (the event is reused after the async hop)
    // Read ALL synchronous channels first (dataTransfer is empty after an await). Priority:
    //  1. the in-webview Tables palette (intra-iframe DnD — fully reliable; the primary "drag a table in" path),
    //  2. text/plain (a native drag MAY deliver it on some platforms),
    //  3. the host stash for a native Model-tree drag (best-effort — VS Code blocks native drops into editor webview
    //     panels by design: it sets pointer-events:none on the iframe during a drag, so this rarely fires. See the
    //     Tables palette / the tree's "Add to diagram" command for the reliable routes).
    const fromPalette = e.dataTransfer?.getData('application/x-semanticus-table') ?? '';
    const fromText = [...((e.dataTransfer?.getData('text/plain') ?? '').matchAll(/'([^']+)'/g))].map((m) => m[1]);
    const dropped = fromPalette ? [fromPalette] : (fromText.length ? fromText : await requestDropTables());
    if (!dropped.length) return;
    if (active.all) { setToast({ text: 'Switch to (or create) a custom diagram to add tables. “All tables” already shows them all.', tone: 'error' }); return; }
    const known = new Set(graph?.tables.map((t) => t.name) ?? []);
    // Duplicate check against the diagram SCOPE, not the kind-filtered view — a filtered-out table is still on
    // the diagram, so re-dropping it must not toast a false "added". revealKinds then makes it visible.
    const inScope = new Set(baseMembers.map((t) => t.name));
    const valid = [...new Set(dropped)].filter((t) => known.has(t));
    revealKinds(valid);
    const toAdd = valid.filter((t) => !inScope.has(t));
    if (!toAdd.length) { setToast({ text: 'Already on this diagram.', tone: 'ok' }); return; }
    addTablesAt(toAdd, rf.screenToFlowPosition({ x: clientX, y: clientY }));
    setToast({ text: `✓ added ${toAdd.length} table${toAdd.length > 1 ? 's' : ''}`, tone: 'ok' });
  }, [active.all, graph, baseMembers, revealKinds, addTablesAt, rf]);

  // Host command "Add to Studio Diagram": add the selected table(s). On a custom diagram → add to it; on "All tables"
  // (already shows everything) → create a new custom diagram seeded with them and switch to it. Reconcile lays them out.
  const addTablesToActive = useCallback((names: string[]) => {
    const known = new Set(graph?.tables.map((t) => t.name) ?? []);
    const valid = [...new Set(names)].filter((n) => known.has(n));
    if (!valid.length) { setToast({ text: 'No matching tables to add.', tone: 'error' }); return; }
    revealKinds(valid);   // an added table must never land invisibly behind a kind filter
    if (active.all) {
      const id = 'd_' + Date.now().toString(36);
      setDiagrams((ds) => [...ds, { id, name: 'Diagram', all: false, tables: valid, positions: {} }]);
      setActiveId(id);
      setToast({ text: `✓ new diagram with ${valid.length} table${valid.length > 1 ? 's' : ''}`, tone: 'ok' });
    } else {
      const novel = valid.filter((n) => !active.tables.includes(n));
      addTables(valid);
      setToast(novel.length ? { text: `✓ added ${novel.length} table${novel.length > 1 ? 's' : ''}`, tone: 'ok' } : { text: 'Already on this diagram.', tone: 'ok' });
    }
  }, [graph, active.all, active.tables, addTables, revealKinds]);

  // Consume a one-shot host add-request exactly once (monotonic nonce; the module-level guard survives remounts so
  // leaving and returning to the tab can't replay a stale request).
  useEffect(() => {
    if (addReq && addReq.nonce > consumedAddNonce) { consumedAddNonce = addReq.nonce; addTablesToActive(addReq.tables); }
  }, [addReq, addTablesToActive]);

  // --- create / edit relationships ---
  const onConnect = useCallback(async (c: Connection) => {
    const colName = (h?: string | null) => (h && h.startsWith('col:') ? h.slice(4, h.lastIndexOf(':')) : null);
    const s = colName(c.sourceHandle), t = colName(c.targetHandle);
    if (!c.source || !c.target || !s || !t) return;   // ignore drags that aren't column→column (e.g. routing anchors)
    if (c.source === c.target) { setToast({ text: 'A relationship needs two different tables.', tone: 'error' }); return; }
    const srcRef = `column:${c.source}/${s}`, tgtRef = `column:${c.target}/${t}`;
    // Pick the ONE (lookup) side. TOM IsKey is rarely set on real key columns, so also treat a *Key/*Id-named column
    // as key-like. If exactly one side looks like a key, that's the ONE side; otherwise default to drag order
    // (source=many → target=one) and let the user fix a wrong guess with the one-click "Swap direction" below.
    const keyish = (ref: string, name: string) => !!colByRef.get(ref)?.isKey || /(?:key|id)$/i.test(name);
    const srcKey = keyish(srcRef, s), tgtKey = keyish(tgtRef, t);
    let manyRef = srcRef, oneRef = tgtRef;
    if (srcKey && !tgtKey) { manyRef = tgtRef; oneRef = srcRef; }   // dragged FROM the key → the source is the one side
    const label = (m: string, o: string) => `${m.slice('column:'.length)} → ${o.slice('column:'.length)}`;
    const swap = (name: string, m: string, o: string) => async () => {
      try { await rpc('setRelationshipCardinality', name, 'One', 'Many'); setToast({ text: `✓ reversed, now ${label(o, m)}`, tone: 'ok' }); await load(); }
      catch (e) { setToast({ text: String((e as Error).message ?? e), tone: 'error' }); }
    };
    // Draw the edge OPTIMISTICALLY *before* the RPC so the line appears the instant you release the drag (the engine
    // round-trip + reload no longer gates the visual). The temp edge is reconciled away by load() on success (the
    // authoritative graph replaces it) and rolled back on failure. The temp name is unique so it can't collide.
    const split = (ref: string) => { const r = ref.slice('column:'.length); const i = r.indexOf('/'); return { table: r.slice(0, i), column: r.slice(i + 1) }; };
    const mm = split(manyRef), oo = split(oneRef);
    const tempName = `__pending__${Date.now().toString(36)}`;
    setGraph((g) => g ? { ...g, relationships: [...g.relationships, { name: tempName, fromTable: mm.table, fromColumn: mm.column, toTable: oo.table, toColumn: oo.column, fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true }] } : g);
    const rollback = () => setGraph((g) => g ? { ...g, relationships: g.relationships.filter((r) => r.name !== tempName) } : g);
    try {
      const newRef = await rpc<string>('createRelationship', manyRef, oneRef);
      const name = newRef?.startsWith('relationship:') ? newRef.slice('relationship:'.length) : newRef;
      setToast({ text: `✓ created ${label(manyRef, oneRef)} (many → one)`, tone: 'ok', action: name ? { label: 'Swap direction', run: swap(name, manyRef, oneRef) } : undefined });
      await load();   // authoritative graph replaces the temp edge (matched by the real name)
    } catch (e) {
      rollback();
      const msg = String((e as Error).message ?? e);
      if (/already exist/i.test(msg)) {
        setToast({ text: 'An active relationship already exists on that column pair.', tone: 'error', action: { label: 'Add inactive', run: async () => {
          try { await rpc('createRelationship', manyRef, oneRef, null, false); setToast({ text: `✓ added inactive ${label(manyRef, oneRef)}`, tone: 'ok' }); await load(); }
          catch (e2) { setToast({ text: String((e2 as Error).message ?? e2), tone: 'error' }); }
        } } });
      } else setToast({ text: msg, tone: 'error' });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [colByRef]);
  const onEdgeDoubleClick = useCallback((_: React.MouseEvent, edge: Edge) => setSelectedRel(edge.id), []);
  const onEdgeClick = useCallback((_: React.MouseEvent, edge: Edge) => setFocusedEdge(edge.id), []);
  const clearFocus = useCallback(() => { setSelectedRel(null); setFocusedEdge(null); }, []);
  const selRel = useMemo(() => graph?.relationships.find((r) => r.name === selectedRel) ?? null, [graph, selectedRel]);

  // Delete a relationship by name. Optimistic (drop the edge immediately) + the engine confirm; a failed delete is
  // surfaced as a toast AND self-heals (the reload re-adds the edge from authoritative state). Used by both the
  // Delete-key shortcut on a focused edge and the props-panel button — so deletion never silently no-ops.
  const deleteRelationship = useCallback(async (name: string) => {
    if (!graph?.relationships.some((r) => r.name === name)) return;
    setGraph((g) => g ? { ...g, relationships: g.relationships.filter((r) => r.name !== name) } : g);
    setSelectedRel(null); setFocusedEdge(null);
    try {
      const res = await rpc<{ changed?: boolean }>('deleteObject', 'relationship:' + name);
      if (res && res.changed === false) setToast({ text: 'That relationship no longer exists.', tone: 'error' });
      else setToast({ text: '✓ relationship deleted', tone: 'ok' });
    } catch (e) {
      setToast({ text: `Couldn't delete relationship: ${String((e as Error).message ?? e)}`, tone: 'error' });
    }
    await load();   // reconcile to authoritative state (restores the edge if the delete didn't take)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [graph]);

  // Click a relationship to focus it, then press Delete/Backspace to remove it (the standard select-then-delete
  // gesture). Ignored while typing in a node's column filter so Backspace there never nukes the focused edge.
  useEffect(() => {
    if (!focusedEdge) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Delete' && e.key !== 'Backspace') return;
      const el = e.target as HTMLElement | null;
      if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable)) return;
      e.preventDefault();
      void deleteRelationship(focusedEdge);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [focusedEdge, deleteRelationship]);

  // Single-click focus: highlight the clicked relationship + its two tables, fade everything else. Derive the
  // display nodes/edges from the base state so the focus styling never pollutes positions or the saved layout.
  const focus = focusedEdge && edges.some((e) => e.id === focusedEdge) ? focusedEdge : null;   // ignore a stale id
  useEffect(() => { if (focusedEdge && !edges.some((e) => e.id === focusedEdge)) setFocusedEdge(null); }, [edges, focusedEdge]);
  const focusEnds = useMemo(() => {
    if (!focus) return null;
    const e = edges.find((x) => x.id === focus);
    return e ? new Set([e.source, e.target]) : null;
  }, [edges, focus]);
  const displayEdges = useMemo(() => {
    if (!focus) return edges;
    return edges.map((e) => e.id === focus
      ? { ...e, zIndex: 10, style: { ...e.style, strokeWidth: 2.5 }, data: { ...(e.data as object), dim: false } }
      : { ...e, animated: false, data: { ...(e.data as object), dim: true } });
  }, [edges, focus]);
  const displayNodes = useMemo(() => {
    if (!focusEnds) return nodes;
    return nodes.map((n) => ({ ...n, style: { ...n.style, opacity: focusEnds.has(n.id) ? 1 : 0.3, transition: 'opacity 120ms' } }));
  }, [nodes, focusEnds]);

  // auto-dismiss a success toast
  useEffect(() => { if (toast?.tone === 'ok') { const id = window.setTimeout(() => setToast(null), 3500); return () => window.clearTimeout(id); } }, [toast]);

  // --- diagram management ---
  const newDiagram = useCallback(() => {
    const id = 'd_' + Date.now().toString(36);
    // Start BLANK (Kane): a new diagram is an empty canvas — add tables via "＋ Add table…". (It used to clone the
    // current view; that surprised people who wanted a clean slate.)
    const d: SavedDiagram = { id, name: 'New diagram', all: false, tables: [], positions: {} };
    setDiagrams((ds) => [...ds, d]);
    setActiveId(id);
    setNameDraft('New diagram');
    setRenaming(true);
  }, []);
  const deleteDiagram = useCallback(() => {
    if (active.all) return;
    setDiagrams((ds) => ds.filter((d) => d.id !== activeId));
    setActiveId(ALL_ID);
  }, [active.all, activeId]);
  const commitRename = useCallback(() => {
    const name = nameDraft.trim();
    if (name) editActive((d) => ({ ...d, name }));
    setRenaming(false);
  }, [nameDraft, editActive]);

  const audit = useMemo(() => {
    if (!graph) return null;
    const used = new Set<string>();
    let bidi = 0, inactive = 0;
    for (const r of graph.relationships) { used.add(r.fromTable); used.add(r.toTable); if (r.crossFilter === 'BothDirections') bidi++; if (!r.isActive) inactive++; }
    const isolated = graph.tables.filter((t) => !used.has(t.name) && !t.isHidden).length;
    return { tables: graph.tables.length, rels: graph.relationships.length, bidi, inactive, isolated };
  }, [graph]);

  // tables not yet on a curated canvas (for the "+ Add table" picker)
  const absent = useMemo(() => {
    if (!graph || active.all) return [];
    const shown = new Set(baseMembers.map((t) => t.name));   // scope, not the kind-filtered set — a hidden-kind table on the diagram isn't "absent"
    return graph.tables.filter((t) => !shown.has(t.name)).map((t) => t.name);
  }, [graph, active.all, baseMembers]);

  if (err) return <div className="p-4"><div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{err}</div></div>;
  if (!graph) return <div className="p-6 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading model graph…</div>;

  return (
    <div className="h-full flex flex-col">
      {/* toolbar */}
      <div className="flex items-center gap-2 px-4 py-2 text-[11px] border-b flex-wrap" style={{ borderColor: 'var(--sem-border)' }}>
        {renaming ? (
          <input autoFocus value={nameDraft} onChange={(e) => setNameDraft(e.target.value)}
            onBlur={commitRename} onKeyDown={(e) => { if (e.key === 'Enter') commitRename(); if (e.key === 'Escape') setRenaming(false); }}
            style={tbInput} />
        ) : (
          <select value={activeId} onChange={(e) => setActiveId(e.target.value)} style={tbInput} title="Switch diagram">
            {diagrams.map((d) => <option key={d.id} value={d.id}>{d.name}{d.all ? '' : ` (${graph.tables.filter((t) => d.tables.includes(t.name)).length})`}</option>)}
          </select>
        )}
        <button onClick={newDiagram} style={tbBtn} title="New blank custom diagram. Add tables with ＋ Add table…">＋ New</button>
        {!active.all && <button onClick={() => { setNameDraft(active.name); setRenaming(true); }} style={tbBtn} title="Rename">✎</button>}
        {!active.all && <button onClick={deleteDiagram} style={tbBtn} title="Delete this diagram">Delete</button>}
        <span style={{ width: 1, height: 16, background: 'var(--sem-border)' }} />
        {!active.all && (
          <select value="" onChange={(e) => { if (e.target.value) addTables([e.target.value]); }} style={tbInput} title="Add a table to this diagram" disabled={absent.length === 0}>
            <option value="">＋ Add table…</option>
            {absent.map((n) => <option key={n} value={n}>{n}</option>)}
          </select>
        )}
        <button onClick={autoArrange} style={layout === 'hierarchy' ? tbBtnActive : tbBtn} title="Vertical: facts left, dimensions right. Tables stack vertically in each band, relationships run as horizontal lanes">Vertical</button>
        <button onClick={layered} style={layout === 'layered' ? tbBtnActive : tbBtn} title="Layered: dimensions across the top, facts underneath. Every relationship is a clean vertical lane">Layered</button>
        <button onClick={busMatrix} style={layout === 'busmatrix' ? tbBtnActive : tbBtn} title="Bus-matrix layout (the default): facts (many side) down the left, dimensions (one side) across the top, unrelated tables on the right">Bus matrix</button>
        <span style={{ width: 1, height: 16, background: 'var(--sem-border)' }} />
        <button onClick={() => setExpandAll(true)} style={tbBtn} title="Expand every table to show its columns (drag column→column to relate)">Expand all</button>
        <button onClick={() => setExpandAll(false)} style={tbBtn} title="Collapse all tables">Collapse all</button>
        <button onClick={() => setSnap((s) => !s)} style={snap ? tbBtnActive : tbBtn} title="Snap dragged tables to a grid for tidy, aligned custom layouts">Snap{snap ? ' ✓' : ''}</button>
        <button onClick={() => rf.fitView({ padding: 0.2, duration: 300 })} style={tbBtn} title="Fit to view">Fit</button>
        <span style={{ width: 1, height: 16, background: 'var(--sem-border)' }} />
        <span style={{ color: 'var(--sem-muted)' }}>Show</span>
        <KindChip on={showData} onClick={() => setShowData((v) => !v)} color="var(--sem-accent)" label="Tables" count={kindCounts.data} title="Show regular data tables" />
        <KindChip on={showCalc} onClick={() => setShowCalc((v) => !v)} color="#17B3A3" label="Calculated" count={kindCounts.calc} title="Show calculated (DAX) tables" />
        <KindChip on={showFp} onClick={() => setShowFp((v) => !v)} color={FIELD_PARAM_VIOLET} label="Field params" count={kindCounts.fp} title="Show field parameters" />
        <span className="ml-auto" style={{ color: 'var(--sem-muted)' }}>{members.length} shown · expand a table to draw relationships</span>
      </div>
      {/* model audit */}
      {audit && (
        <div className="flex items-center gap-4 px-4 py-2 text-[11px] border-b" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
          <Stat label="tables" value={audit.tables} />
          <Stat label="relationships" value={audit.rels} />
          <Stat label="bidirectional" value={audit.bidi} warn={audit.bidi > 0} />
          <Stat label="inactive" value={audit.inactive} />
          <Stat label="isolated" value={audit.isolated} warn={audit.isolated > 0} />
          <div className="ml-auto flex items-center gap-3">
            <CardGlyph kind="many" /> many
            <CardGlyph kind="one" /> one
            <span style={{ width: 1, height: 12, background: 'var(--sem-border)' }} />
            <Legend color="var(--sem-accent)" text="1-way" />
            <Legend color="var(--sem-warn)" text="bi-directional" />
            <Legend color="var(--sem-muted)" text="inactive" dashed />
          </div>
        </div>
      )}
      <CardinalityMarkers />
      {/* The canvas is ALWAYS mounted (even when empty) so it's a live drop target for tables dragged from the Model
          tree; the empty-state hint is an overlay on top (pointer-events:none so it never blocks a drop). */}
      <div className="flex-1 min-h-0 relative" onDragOver={onCanvasDragOver} onDrop={onCanvasDrop}>
        {!active.all && absent.length > 0 && <TablePalette tables={absent} onAdd={(n) => addTables([n])} />}
        {selRel && <RelPropsPanel rel={selRel} onClose={() => setSelectedRel(null)} onChanged={load} onDelete={deleteRelationship} />}
        {toast && (
          <div className="absolute left-1/2 -translate-x-1/2 bottom-3 z-10 flex items-center gap-2 rounded-md border px-3 py-1.5 text-[11px] shadow-lg"
            style={{ background: 'var(--sem-surface)', borderColor: toast.tone === 'ok' ? 'var(--sem-good)' : 'var(--sem-bad)', color: 'var(--sem-fg)' }}>
            <span>{toast.text}</span>
            {toast.action && <button onClick={() => { const a = toast.action!; setToast(null); void a.run(); }} style={tbBtn}>{toast.action.label}</button>}
            <button onClick={() => setToast(null)} style={{ background: 'transparent', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer' }}>✕</button>
          </div>
        )}
        <ReactFlow
          nodes={displayNodes} edges={displayEdges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
          onNodesChange={onNodesChange} onNodeDrag={onNodeDrag} onNodeDragStop={commitPositions}
          onConnect={onConnect} onEdgeClick={onEdgeClick} onEdgeDoubleClick={onEdgeDoubleClick} onPaneClick={clearFocus}
          connectionMode={ConnectionMode.Loose} connectionLineComponent={RelConnectionLine}
          snapToGrid={snap} snapGrid={[GRID, GRID]}
          fitView minZoom={0.15} proOptions={{ hideAttribution: true }}
        >
          <Background color="var(--sem-border)" gap={20} />
          <Controls showInteractive={false} />
        </ReactFlow>
        {members.length === 0 && (
          <div className="absolute inset-0 flex items-center justify-center text-[12px] pointer-events-none px-6 text-center" style={{ color: 'var(--sem-muted)' }}>
            {baseMembers.length > 0
              ? 'All tables hidden by filters. Re-enable a type under “Show” above.'
              : 'Empty diagram. Drag tables from the “Add tables” palette, use “＋ Add table…” above, or select a table and “＋ related”.'}
          </div>
        )}
      </div>
    </div>
  );
}

const tbInput: React.CSSProperties = {
  background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)',
  border: '1px solid var(--sem-border)', borderRadius: 4, fontSize: 11, padding: '2px 6px', maxWidth: 220,
};

// The Diagram tab hosts two views of the model's relationships: the interactive ER Canvas and a sortable
// Relationships grid (relocated here when the old "Audit" tab was retired). A segmented toggle switches between
// them; the choice persists. Both read the same live model graph, so they stay in sync.
// `addTables` is a one-shot request from the host (Model-tree "Add to Studio Diagram") carrying the table(s) to drop
// onto the canvas, with a nonce so a repeat add re-fires. Forwarded to DiagramInner, which consumes it once.
export function DiagramView({ addTables }: { addTables?: { tables: string[]; nonce: number } | null }) {
  const [view, setView] = useState<'canvas' | 'rels'>(() => loadState<'canvas' | 'rels'>('diagramView', 'canvas'));
  useEffect(() => { saveState('diagramView', view); }, [view]);
  return (
    <div className="h-full flex flex-col">
      <div className="flex items-center gap-1 px-4 pt-2 pb-1.5 border-b" style={{ borderColor: 'var(--sem-border)' }}>
        <ViewToggle active={view === 'canvas'} onClick={() => setView('canvas')}>◈ Canvas</ViewToggle>
        <ViewToggle active={view === 'rels'} onClick={() => setView('rels')}>▤ Relationships</ViewToggle>
        <span className="text-[11px] ml-2" style={{ color: 'var(--sem-muted)' }}>
          {view === 'canvas' ? 'Drag from “Add tables” onto the canvas · expand a table to draw relationships · click a relationship then Delete to remove' : 'Every relationship: cardinality, cross-filter, active flag, and common problems'}
        </span>
      </div>
      <div className="flex-1 min-h-0">
        {view === 'canvas' ? <ReactFlowProvider><DiagramInner addReq={addTables ?? null} /></ReactFlowProvider> : <RelationshipsView />}
      </div>
    </div>
  );
}

function ViewToggle({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button onClick={onClick} className="text-[12px] px-2.5 py-1 rounded-md font-medium"
      style={active ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)' }}>
      {children}
    </button>
  );
}

function Stat({ label, value, warn }: { label: string; value: number; warn?: boolean }) {
  return <div className="flex items-baseline gap-1"><span className="font-semibold tnum" style={{ color: warn ? 'var(--sem-warn)' : 'var(--sem-fg)' }}>{value}</span><span>{label}</span></div>;
}
// A compact table-kind toggle in the diagram toolbar. The swatch matches that kind's node accent (green data, teal
// calc, violet field-parameter) so the chip reads as "show these". ON = filled swatch + active button; OFF = hollow
// swatch + dimmed. Toggling hides the whole kind (and its relationship lines) from the canvas.
function KindChip({ on, onClick, color, label, count, title }: { on: boolean; onClick: () => void; color: string; label: string; count: number; title: string }) {
  return (
    <button onClick={onClick} title={title} aria-pressed={on}
      style={{ ...(on ? tbBtnActive : tbBtn), display: 'inline-flex', alignItems: 'center', gap: 5, opacity: on ? 1 : 0.6 }}>
      <span style={{ width: 8, height: 8, borderRadius: 2, background: on ? color : 'transparent', border: `1px solid ${color}` }} />
      {label}<span className="tnum" style={{ color: 'var(--sem-muted)' }}>{count}</span>
    </button>
  );
}
// Mini crow's-foot / bar swatch for the legend (drawn directly, not via the edge markers).
function CardGlyph({ kind }: { kind: 'many' | 'one' }) {
  return (
    <svg width="22" height="12" style={{ overflow: 'visible' }} aria-hidden>
      <line x1="0" y1="6" x2="18" y2="6" style={{ stroke: 'var(--sem-accent)', strokeWidth: 1.5 }} />
      {kind === 'many'
        ? <path d="M22,6 L12,1 M22,6 L12,11" style={{ stroke: 'var(--sem-accent)', strokeWidth: 1.5, fill: 'none' }} />
        : <line x1="15" y1="1" x2="15" y2="11" style={{ stroke: 'var(--sem-accent)', strokeWidth: 1.5 }} />}
    </svg>
  );
}
function Legend({ color, text, dashed }: { color: string; text: string; dashed?: boolean }) {
  return <span className="flex items-center gap-1"><span style={{ width: 14, height: 0, borderTop: `2px ${dashed ? 'dashed' : 'solid'} ${color}` }} />{text}</span>;
}

// In-canvas relationship properties panel (double-click an edge). Endpoints are read-only; cross-filter, cardinality
// (set_relationship_cardinality), active, and delete are editable via the existing engine ops.
function RelPropsPanel({ rel, onClose, onChanged, onDelete }: { rel: GraphRelationship; onClose: () => void; onChanged: () => Promise<void> | void; onDelete: (name: string) => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Surface failures instead of swallowing them (the old empty catch hid e.g. a failed cardinality/active change).
  const run = async (fn: () => Promise<unknown>) => { setBusy(true); setError(null); try { await fn(); await onChanged(); } catch (e) { setError(String((e as Error).message ?? e)); } finally { setBusy(false); } };
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <div className="absolute top-2 right-2 z-10 rounded-lg border shadow-lg text-[11px]" style={{ width: 264, background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2 px-3 py-2 border-b" style={{ borderColor: 'var(--sem-border)' }}>
        <span className="font-semibold">Relationship</span>
        <button className="ml-auto" onClick={onClose} style={tbBtn} title="Close (Esc)">✕</button>
      </div>
      <div className="px-3 py-2 flex flex-col gap-2">
        <div className="font-mono" style={{ color: 'var(--sem-muted)', fontSize: 10.5 }}>{rel.fromTable}[{rel.fromColumn}] → {rel.toTable}[{rel.toColumn}]</div>
        <PropRow label="Cardinality">
          <select value={`${rel.fromCardinality}|${rel.toCardinality}`} disabled={busy} style={{ ...tbInput, maxWidth: 150 }}
            onChange={(e) => { const [f, t] = e.target.value.split('|'); run(() => rpc('setRelationshipCardinality', rel.name, f, t)); }}>
            <option value="Many|One">Many → One</option>
            <option value="One|Many">One → Many</option>
            <option value="One|One">One → One</option>
            <option value="Many|Many">Many → Many</option>
          </select>
        </PropRow>
        <PropRow label="Cross-filter">
          <select value={rel.crossFilter === 'BothDirections' ? 'BothDirections' : 'OneDirection'} disabled={busy} style={{ ...tbInput, maxWidth: 150 }}
            onChange={(e) => run(() => rpc('setRelationship', rel.name, e.target.value, null))}>
            <option value="OneDirection">Single</option>
            <option value="BothDirections">Both directions</option>
          </select>
        </PropRow>
        <label className="flex items-center gap-2"><input type="checkbox" checked={rel.isActive} disabled={busy} onChange={(e) => run(() => rpc('setRelationship', rel.name, null, e.target.checked))} /> Active</label>
        <button onClick={() => { onClose(); onDelete(rel.name); }} disabled={busy}
          style={{ ...tbBtn, color: 'var(--sem-bad)', borderColor: 'var(--sem-bad)', marginTop: 2 }}>Delete relationship</button>
        {error && <div style={{ color: 'var(--sem-bad)', fontSize: 10.5 }}>{error}</div>}
      </div>
    </div>
  );
}
// In-canvas Tables palette: the RELIABLE way to add a table to a custom diagram. VS Code blocks native Model-tree
// drags from dropping into an editor webview panel (it sets pointer-events:none on the iframe during a drag), so we
// give an in-webview source instead — dragging from here to the canvas is plain intra-iframe HTML5 DnD, which works.
// Lists the model's tables not yet on this diagram; drag one onto the canvas (lands at the drop point) or double-click
// to add. Stays in sync because `absent` is derived from the live graph.
function TablePalette({ tables, onAdd }: { tables: string[]; onAdd: (name: string) => void }) {
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(true);
  const shown = useMemo(() => { const s = q.trim().toLowerCase(); return s ? tables.filter((t) => t.toLowerCase().includes(s)) : tables; }, [tables, q]);
  return (
    <div className="absolute top-2 left-2 z-10 rounded-lg border shadow-lg text-[11px] flex flex-col" style={{ width: 184, maxHeight: 'calc(100% - 16px)', background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-1.5 px-2.5 py-1.5"
        style={{ borderBottom: open ? '1px solid var(--sem-border)' : 'none', background: 'transparent', color: 'var(--sem-fg)', cursor: 'pointer' }} title="Drag a table onto the canvas to add it">
        <span style={{ fontSize: 10, width: 10 }}>{open ? '▾' : '▸'}</span>
        <span className="font-semibold">Add tables</span>
        <span className="ml-auto" style={{ color: 'var(--sem-muted)' }}>{tables.length}</span>
      </button>
      {open && (
        <>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="filter tables…"
            style={{ ...tbInput, margin: '6px 6px 4px', maxWidth: 'unset' }} />
          <div className="nowheel" style={{ overflowY: 'auto', overflowX: 'hidden', padding: '0 6px 6px' }}>
            {shown.map((t) => (
              <div key={t} draggable onDragStart={(e) => { e.dataTransfer.setData('application/x-semanticus-table', t); e.dataTransfer.effectAllowed = 'copy'; }}
                onDoubleClick={() => onAdd(t)} title="Drag onto the canvas (or double-click to add)" className="truncate flex items-center gap-1"
                style={{ padding: '3px 7px', marginBottom: 3, borderRadius: 4, cursor: 'grab', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>
                <span aria-hidden style={{ color: 'var(--sem-muted)', fontSize: 9 }}>⠿</span><span className="truncate">{t}</span>
              </div>
            ))}
            {shown.length === 0 && <div style={{ color: 'var(--sem-muted)', padding: '2px 4px' }}>no matches</div>}
          </div>
        </>
      )}
    </div>
  );
}
function PropRow({ label, children }: { label: string; children: React.ReactNode }) {
  return <div className="flex items-center gap-2"><span className="w-20 shrink-0" style={{ color: 'var(--sem-muted)' }}>{label}</span>{children}</div>;
}
