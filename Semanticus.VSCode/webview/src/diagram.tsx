import { useEffect, useLayoutEffect, useMemo, useRef, useState, useCallback } from 'react';
import {
  ReactFlow, ReactFlowProvider, Background, Controls, Handle, Position, ConnectionMode,
  NodeToolbar, useNodesState, useEdgesState, useReactFlow, useUpdateNodeInternals, useStoreApi, getViewportForBounds,
  type Node, type Edge, type NodeProps, type Connection,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import dagre from '@dagrejs/dagre';
import { rpc, onDidChange, onLayoutChange, loadState, saveState, requestDropTables, type LayoutData } from './bridge';
import { edgeTypes, RelConnectionLine, BusRoutes, runInSlices } from './diagramedges';
import { RelationshipsView } from './relationships';
import { CANVAS_CHIP_MIN_H, CanvasChip, RowMenu, RowSep, ToolRow, canvasInsetStyle, useCanvasBox, useToolRow } from './toolrow';
import type { ColumnRow } from './wire';
import { uiLabel } from './copy';
import { NODE_W, NODE_H, sizeForAnchors, classifyRoles, baryOrder, roleOf, computeAnchors, busMatrixPositions, type Anchor, type Side } from './diagramlayout.mjs';
import { reflowCards, clearDropped, roomSteps, keepBeside, linesBySide, CARD_GAP, changedPositions, toggleExpansion, setExpansionFor, layoutBox, boxChanges, sizesToCompare, changeReloadsDiagram, isLayoutChange, isArrangeChange, spreadStart, spreadKey, savedAfterDrag, savedSpots, filterHeldHeight, recordsHeight, readableFirstView, type FitSize, type SideLines } from './diagramfit.mjs';

// The page's own how-to sentences. They used to sit on the toolbar as a long grey line, which cost the canvas a
// whole row on every screen for something you read once. They live here as named constants so the tooltip of the
// control they describe and the shell's page-notes popover read the SAME words, and neither can drift.
export const DIAGRAM_CANVAS_HINT = 'Drag a table from the Add tables list onto the canvas. Open a table to draw a link between two of its columns. Click a link, then press Delete, to remove it.';
export const DIAGRAM_RELATIONSHIPS_HINT = 'Every link between two tables: how the rows match up, which way the filtering flows, whether it is switched on, and the common problems.';
export const DIAGRAM_NOTES: string[] = [DIAGRAM_CANVAS_HINT, DIAGRAM_RELATIONSHIPS_HINT];

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
function plainRelType(t: string): string {
  switch (t.toLowerCase()) {
    case 'int64': return 'whole number';
    case 'string': return 'text';
    case 'datetime': return 'date';
    case 'boolean': return 'true/false';
    case 'decimal': return 'decimal';
    case 'double': return 'decimal number';
    default: return t;
  }
}

const GRID = 20;   // snap-to-grid step (matches the Background dot gap)

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
// `expanded` is legacy: expansion used to live per diagram and was thrown away by every arrange. It is now one
// per-table map for the whole canvas (diagramExpanded), and this field is only read once, to seed that map.
// `keep`: the cards the person (a put-back) or their assistant (save_layout's keep) asked to leave where they are when
// the Diagram makes room for the lines. The All-tables diagram mirrors the engine layout's keep marks; a move clears one.
interface SavedDiagram { id: string; name: string; all: boolean; tables: string[]; positions: Record<string, XY>; layout?: LayoutMode; expanded?: Record<string, boolean>; keep?: Record<string, true>; }

const ALL_ID = 'all';
const DEFAULT_DIAGRAM: SavedDiagram = { id: ALL_ID, name: 'All tables', all: true, tables: [], positions: {} };
const uniq = (xs: string[]) => Array.from(new Set(xs));

// A relationship end's connection point on a node side. `frac` evenly spreads the K ends sharing a side into K
// distinct anchors so no two edges enter/exit at the same point. `rel` matches the edge's sourceHandle/targetHandle.
type Toast = { text: string; tone: 'ok' | 'error'; action?: { label: string; run: () => void | Promise<void> } };
const EMPTY_SET = new Set<string>();          // stable identities so a table with no related/anchor data doesn't churn
const EMPTY_ANCHORS: Anchor[] = [];

type GraphCardData = {
  t: GraphTable; rels: number; curated: boolean;
  anchors: Anchor[]; w: number; h: number;   // dynamic size: grows with incident-relationship count per side
  layout: LayoutMode;            // the layout this card's anchors and box were made for (an arrange lands before the rebuild does)
  expanded: boolean;
  columns: ColumnRow[] | null;   // the table's columns, only when expanded (for drag-to-create)
  related: Set<string>;          // column names already in a relationship — sorted to the top, above a divider
  filter: string;                // per-node column search text
  onAddRelated: (name: string) => void; onRemove: (name: string) => void;
  onToggleExpand: (name: string) => void; onFilter: (name: string, q: string) => void;
};

// Position an (invisible) relationship anchor handle along its node side: frac runs the long axis of the side.
function anchorStyle(side: Side, frac: number): React.CSSProperties {
  const base: React.CSSProperties = { width: 8, height: 8, opacity: 0, border: 'none', background: 'transparent' };
  return side === 'top' || side === 'bottom' ? { ...base, left: `${frac * 100}%` } : { ...base, top: `${frac * 100}%` };
}

// An opened card lifts off the canvas with a shadow (the dialogs' shadow) and sits above its neighbours (OPEN_CARD_Z). While
// the page works out where the cards go to make space for it (the spread, below) it can still cover a neighbour for a moment.
const OPEN_CARD_SHADOW = '0 8px 24px rgba(0,0,0,.28)';
const OPEN_CARD_Z = 5;

function TableNode({ data, selected }: NodeProps<Node<GraphCardData>>) {
  const t = data.t;
  // Instrument (23 September): a table's role is a quiet word in its header (date, field parameter, calculated,
  // hidden), not a coloured wash and a coloured pill. The lines between tables keep their colours, because those
  // are the diagram's data. A field parameter IS a calc table, so it must be tested before the plain-calc branch.
  // Kane's field-parameter identity (violet plus an FP marker) stays, as a dot and a word.
  const dot = t.isDateTable ? 'var(--sem-warn)' : t.isFieldParameter ? FIELD_PARAM_VIOLET : t.isCalculated ? 'var(--sem-muted)' : null;

  // Expanded column list: key + already-related columns first, then a divider, then the rest. Search filters across all.
  const cols = useMemo(() => {
    if (!data.expanded || !data.columns) return null;
    const q = data.filter.trim().toLowerCase();
    const matched = data.columns.filter((c) => !q || c.name.toLowerCase().includes(q));
    const isPrimary = (c: ColumnRow) => c.isKey || data.related.has(c.name);
    const byName = (a: ColumnRow, b: ColumnRow) => a.name.localeCompare(b.name);
    return { primary: matched.filter(isPrimary).sort(byName), rest: matched.filter((c) => !isPrimary(c)).sort(byName) };
  }, [data.expanded, data.columns, data.filter, data.related]);

  // Hold the card's height while its column filter is active, so typing never moves another card (diagramfit.mjs).
  // offsetHeight is in layout pixels, unaffected by the canvas zoom.
  const cardRef = useRef<HTMLDivElement>(null);
  const heldH = useRef<number | undefined>(undefined);
  useLayoutEffect(() => {
    if (recordsHeight(data.expanded, data.filter) && cardRef.current) heldH.current = cardRef.current.offsetHeight;
  });
  const hold = filterHeldHeight(data.expanded, data.filter, heldH.current);

  return (
    <div ref={cardRef} data-object-ref={`table:${t.name}`} className="rounded-(--sem-radius-card) border text-(length:--sem-text-label) flex flex-col" style={{
      width: data.w, minHeight: Math.max(data.h, hold ?? 0), background: 'var(--sem-surface)',
      borderColor: selected ? 'var(--sem-strong-border)' : 'var(--sem-border)',
      boxShadow: [selected && '0 0 0 1px var(--sem-fg)', data.expanded && OPEN_CARD_SHADOW].filter(Boolean).join(', ') || 'var(--sem-elev)',
    }}>
      {data.curated && (
        <NodeToolbar isVisible={selected} position={Position.Top} className="flex gap-1">
          <button onClick={() => data.onAddRelated(t.name)} title="Add tables related to this one" style={tbBtn}>＋ related</button>
          <button onClick={() => data.onRemove(t.name)} title="Remove from this diagram" style={tbBtn}>✕ remove</button>
        </NodeToolbar>
      )}
      {/* Relationship anchor handles — one per incident relationship end, invisible, pure routing geometry (not connectable). */}
      {data.anchors.map((a) => (
        <Handle key={a.rel + ':' + a.kind} id={a.rel + ':' + a.kind} type={a.kind} position={a.side as Position} isConnectable={false} style={anchorStyle(a.side, a.frac)} />
      ))}
      <div className="px-3 py-1 flex items-center gap-2" style={{ borderBottom: '1px solid var(--sem-rule)' }}>
        <button className="nodrag" onClick={() => data.onToggleExpand(t.name)} title={data.expanded ? 'Hide columns' : 'Show columns to connect'}
          aria-label={data.expanded ? `Hide the columns of ${t.name}` : `Show the columns of ${t.name}`}
          style={{ background: 'transparent', border: 'none', color: 'var(--sem-fg)', cursor: 'pointer', padding: 0, fontSize: 'var(--sem-text-label)', width: 12 }}>{data.expanded ? '▾' : '▸'}</button>
        <span className="font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{t.name}</span>
        <span className="ml-auto flex items-center gap-1 shrink-0" style={{ color: 'var(--sem-muted)' }}>
          {t.isHidden && <span title="Hidden from report view">hidden</span>}
          {dot && <span className="w-1 h-1 rounded-full shrink-0" style={{ background: dot }} />}
          {/* A field parameter is a calc table: show the more specific FP marker instead of calculated, never both. */}
          {t.isDateTable ? <span>date</span>
            : t.isFieldParameter ? <span title="Field parameter">FP</span>
            : t.isCalculated && <span>calculated</span>}
        </span>
      </div>
      {!data.expanded ? (
        <div className="px-3 py-1 flex flex-col gap-1" style={{ color: 'var(--sem-muted)' }}>
          <div className="flex gap-3">
            <span><span className="sem-num">{t.columns}</span> cols</span>
            {/* A zero says nothing and "isolated" repeats the "not linked" chip, so neither is drawn. */}
            {t.measures > 0 && <span><span className="sem-num">{t.measures}</span> msr</span>}
          </div>
          {t.keyColumns.length > 0 && (
            <div className="truncate" title={t.keyColumns.join(', ')}>key: <span style={{ color: 'var(--sem-fg)' }}>{t.keyColumns.join(', ')}</span></div>
          )}
        </div>
      ) : (
        <div className="py-1">
          <input className="nodrag" value={data.filter} onChange={(e) => data.onFilter(t.name, e.target.value)} placeholder="filter columns…"
            style={{ width: 'calc(100% - 16px)', margin: '0 var(--sem-space-2) var(--sem-space-2)', padding: 'var(--sem-space-1) var(--sem-space-2)', fontSize: 'var(--sem-text-label)', background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)' }} />
          <div className="nowheel" style={{ maxHeight: 220, overflowY: 'auto', overflowX: 'hidden' }}>
            {cols && cols.primary.map((c) => <ColRow key={c.name} c={c} />)}
            {cols && cols.primary.length > 0 && cols.rest.length > 0 && <div style={{ height: 1, background: 'var(--sem-border)', margin: 'var(--sem-space-1) var(--sem-space-2)' }} />}
            {cols && cols.rest.map((c) => <ColRow key={c.name} c={c} />)}
            {cols && cols.primary.length + cols.rest.length === 0 && <div style={{ color: 'var(--sem-muted)', padding: 'var(--sem-space-1) var(--sem-space-3)' }}>no matching columns</div>}
          </div>
          <div style={{ color: 'var(--sem-muted)', fontSize: 'var(--sem-text-label)', padding: 'var(--sem-space-1) var(--sem-space-3) 0' }}>drag a column → another table's column to relate</div>
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
    <div data-object-ref={c.ref} className="nodrag relative flex items-center gap-1 sem-colrow" style={{ padding: 'var(--sem-space-1) var(--sem-space-3)', fontSize: 'var(--sem-text-label)', cursor: 'crosshair' }}
      title="Drag this column onto another table's column to create a relationship">
      <Handle type="source" id={`col:${c.name}:right`} position={Position.Right}
        style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', transform: 'none', borderRadius: 0, background: 'transparent', border: 'none', zIndex: 1 }} />
      {c.isKey && <span>key</span>}
      <span className="truncate" style={{ color: c.isHidden ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>{c.name}</span>
      <span className="ml-auto shrink-0 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{c.dataType}</span>
      <span className="shrink-0" style={{ opacity: 0.55, fontSize: 'var(--sem-text-label)', lineHeight: 1, zIndex: 2 }} aria-hidden>⇄</span>
    </div>
  );
}

const tbBtn: React.CSSProperties = {
  background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)',
  borderRadius: 'var(--sem-radius-card)', fontSize: 'var(--sem-text-label)', padding: 'var(--sem-space-1) var(--sem-space-2)', cursor: 'pointer',
};

const nodeTypes = { table: TableNode };
// React Flow's fit margin: a fraction of the viewport left around the fitted graph, not a CSS length.
const FIT_MARGIN = 0.2;
// Screen px left clear at the edge a readable first frame starts from (the page margin, --sem-page-margin).
const FIRST_FRAME_PAD = 24;
// The canvas's own zoom floor, shared by the ReactFlow prop and the first-frame fit sum.
const MIN_ZOOM = 0.15;

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

// Each relationship END's ORDER (0-based) along the node side it sits on + that fan's size, derived from the
// POSITION-sorted anchors. The elbow offset uses this so a fan's lines turn on distinct, SEQUENTIAL lanes — e.g. in
// the layered view each dim's vertical drop length grows with its sequence, staggering the horizontal segments so
// they never collapse onto one line. (Keyed table|rel|kind to match the source/target end of an edge.)
function anchorSeqIndex(anchors: Map<string, Anchor[]>): Map<string, { i: number; n: number }> {
  const out = new Map<string, { i: number; n: number }>();
  for (const [table, arr] of anchors) {
    const bySide = new Map<Side, Anchor[]>();
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
const layoutToKeep = (tables: { name: string; keep?: boolean }[] | undefined): Record<string, true> => {
  const k: Record<string, true> = {};
  for (const t of tables ?? []) if (t.name && t.keep) k[t.name] = true;
  return k;
};
const positionsToLayoutNodes = (pos: Record<string, XY>, keep = false) =>
  Object.entries(pos).map(([name, p]) => (keep ? { name, x: p.x, y: p.y, keep: true } : { name, x: p.x, y: p.y }));
// Every card's box as the reflow and make-room helpers take it (only measured cards: an unmeasured one has no size yet).
const cardsOf = (nodes: Node[]) => nodes.filter((n) => n.measured?.width && n.measured?.height)
  .map((n) => ({ id: n.id, x: n.position.x, y: n.position.y, w: n.measured!.width!, h: n.measured!.height! }));
// How many lines leave each card by each side, from its anchors: the room makeRoom keeps beside that side.
const linesOf = (nodes: Node[]) => new Map<string, SideLines>(nodes.map((n) => [n.id, linesBySide((n.data as { anchors?: { side: string }[] }).anchors)]));

// Highest add-request nonce already consumed — module-level so it survives DiagramInner remounts (the canvas
// unmounts when you leave the tab), preventing a stale request from re-firing (and re-creating a diagram) on return.
let consumedAddNonce = 0;

function DiagramInner({ addReq, focusReq, onAddConsumed, modeSwitch }: {
  addReq: { tables: string[]; nonce: number } | null;
  focusReq?: { id: string; nonce: number } | null;
  onAddConsumed?: (nonce: number) => void;
  // The Canvas / Relationships switch. It leads this tool's ONE row rather than owning a row of its own, so
  // the two views share a single 40px strip instead of stacking two.
  modeSwitch?: React.ReactNode;
}) {
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
  // Which tables are open, per TABLE and for the whole canvas: it survives a layout change and a switch between
  // saved diagrams (Kane, 25 Sep). The person's current expansion always wins; a saved diagram does not carry its
  // own. The first load seeds it from the active diagram's legacy per-diagram map so nobody's open cards vanish.
  const [expanded, setExpanded] = useState<Record<string, boolean>>(() => {
    const saved = loadState<Record<string, boolean> | null>('diagramExpanded', null);
    if (saved) return saved;
    const id = loadState<string>('activeDiagramId', ALL_ID);
    return loadState<SavedDiagram[]>('diagrams', [DEFAULT_DIAGRAM]).find((d) => d.id === id)?.expanded ?? {};
  });

  const [nodes, setNodes, onNodesChange] = useNodesState<Node<GraphCardData>>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);
  const rf = useReactFlow();
  const flowStore = useStoreApi();
  // The canvas's shape (width over height). The bus matrix passes it on but no longer wraps its row to it (Kane, 8 Oct).
  const canvasAspect = useCallback(() => { const { width, height } = flowStore.getState(); return width > 0 && height > 0 ? width / height : undefined; }, [flowStore]);
  const updateNodeInternals = useUpdateNodeInternals();

  // --- viewport framing (NOT a layout write: fitView only moves the camera, saved positions are untouched) ---
  // True once the person has panned/zoomed since the LAST fit. The deferred first-open fit checks this so it can
  // never yank the camera away from a view the person set up themselves while the model was still loading.
  const interactedSinceFit = useRef(false);
  const didFirstFit = useRef(false);
  const doFit = useCallback((opts?: Parameters<typeof rf.fitView>[0]) => {
    try { rf.fitView(opts ?? { padding: FIT_MARGIN, duration: 300 }); } catch { /* not mounted */ }
    interactedSinceFit.current = false;   // a completed fit is the new baseline the interaction guard measures from
  }, [rf]);
  // Fit only once the nodes have been MEASURED. React Flow fills `node.measured` on a render pass AFTER the nodes are
  // set, and a table card's height depends on its anchors (and on updateNodeInternals re-measuring them). Fitting
  // before that frames a box built from unmeasured/stale sizes, which is what left the graph parked in the lower
  // third of an otherwise empty canvas on first open at 1440x900. Poll a few frames, then fit anyway so a node that
  // never reports a size cannot mean no fit at all.
  // The FIRST frame keeps the cards readable (journey m11 F17): a whole bus matrix fitted into a narrow Studio drew its
  // text at 3 to 4 screen px. When the full fit would go below a readable zoom, the frame holds that zoom from the
  // diagram's top-left instead (diagramfit.mjs readableFirstView). Fit on the row still shows the whole diagram.
  const readableFit = useCallback((opts?: Parameters<typeof rf.fitView>[0]) => {
    const { width, height } = flowStore.getState();
    const ns = rf.getNodes();
    const bounds = ns.length ? rf.getNodesBounds(ns) : null;
    const full = bounds && width > 0 && height > 0 ? getViewportForBounds(bounds, width, height, MIN_ZOOM, 2, FIT_MARGIN) : null;
    const view = bounds && full ? readableFirstView(bounds, width, height, full.zoom, FIRST_FRAME_PAD) : null;
    if (!view) { doFit(opts); return; }
    try { void rf.setViewport(view); } catch { /* not mounted */ }
    interactedSinceFit.current = false;
  }, [rf, flowStore, doFit]);
  const fitWhenMeasured = useCallback((opts?: Parameters<typeof rf.fitView>[0], readable = false) => {
    let tries = 0;
    const tick = () => {
      const ns = rf.getNodes();
      const ready = ns.length > 0 && ns.every((n) => (n.measured?.width ?? 0) > 0 && (n.measured?.height ?? 0) > 0);
      if (ready || tries++ > 30) { if (readable) readableFit(opts); else doFit(opts); return; }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }, [rf, doFit, readableFit]);

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
  const expandedKey = JSON.stringify(expanded);

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
  useEffect(() => { saveState('diagramExpanded', expanded); }, [expanded]);
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
        setDiagrams((ds) => ds.map((d) => (d.id === ALL_ID ? { ...d, positions: { ...d.positions, ...pos }, keep: layoutToKeep(lay?.tables) } : d)));
      setGraph(g); setColumns(cols); setErr(null);
    } catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  useEffect(() => {
    void load();
    let timer: number | undefined;
    const off = onDidChange((n) => {
      if (changeReloadsDiagram(n)) {
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
      // The broadcast carries every stored entry, so its keep marks replace ours.
      setDiagrams((ds) => ds.map((d) => (d.id === ALL_ID ? { ...d, positions: { ...d.positions, ...pos }, keep: layoutToKeep(v.tables) } : d)));
      if (allActiveRef.current) {
        // The assistant's layout may leave cards on top of each other or too close for their lines: once the moved
        // cards are measured, the settle pass below makes room, as it does after a drop (Kane, 28 Sep).
        // Round 9: the layout it saved ends the spread first (every card back on its saved spot, the assistant's on top), and
        // the make-room then runs at once, as it did, before the spread is drawn again for the cards still open.
        roomUndo.current = null; setPutBack(0);
        const was = endSpread(); dropRoom();
        settleAll.current = true; settleNow.current = true;
        setNodes((nds) => nds.map((n) => { const p = pos[n.id] ?? was?.[n.id]; return p ? { ...n, position: p } : n; }));
      }
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
  const onToggleExpand = useCallback((name: string) => setExpanded((e) => toggleExpansion(e, name)), []);
  const onFilter = useCallback((name: string, q: string) => setFilters((f) => ({ ...f, [name]: q })), []);
  const setExpandAll = useCallback((on: boolean) =>
    setExpanded((e) => setExpansionFor(e, members.map((t) => t.name), on)), [members]);

  // Reconcile the canvas whenever the graph reloads, the diagram switches, or membership changes.
  // Keeps live (un-saved) drag positions on a graph reload of the SAME diagram; uses the diagram's
  // saved positions on a switch. New tables are placed without disturbing the existing layout.
  const prevActive = useRef(activeId);
  // The spread (Kane, 29 Sep, "Slide down"): while any card is open, the cards are DRAWN where the layout would put them to make
  // space for the open ones, and nothing of that is the layout. `drawn` is where each card is drawn now, `key` what the picture was
  // worked out from (spreadKey), `done` false while the lines' room is still being worked out. The flow store holds the drawn
  // spots, so the SAVED ones are held apart, in `held` (round 11, R10-1: round 10 kept them inside the spread, one of the spread's
  // own guards cleared them, and the rebuild that followed saved the drawn spots as the layout). Every path that saves or reads a
  // card's saved spot goes through `held` (persistPositions refuses a drawn one). Nothing the spread does can clear it: only a
  // page-level event that puts every card back on its saved spot does, through endSpread.
  type Spread = { drawn: Record<string, XY>; key: string; done: boolean };
  const spread = useRef<Spread | null>(null);
  const held = useRef(savedSpots()).current;
  // The spread is over: the caller puts every card on the saved spots it returns (null when no spread was drawn).
  const endSpread = () => { spread.current = null; return held.release(); };
  const spreadWorking = useRef(false);   // the spread's room for the lines is running on roomStop
  const anyOpenRef = useRef(false);      // some card is open as of the last render (a spread's late result checks it before it is drawn)
  const settleNow = useRef(false);       // the make-room runs at once even with cards open (an arrange, the assistant's save, tables added)
  const [spreadTick, setSpreadTick] = useState(0);   // bumped when a make-room ends, so the spread is worked out again
  const lastRebuild = useRef<{ graph: unknown; activeId: string; membershipKey: string; layout: string } | null>(null);
  useEffect(() => {
    if (!graph) return;
    const switched = prevActive.current !== activeId;
    prevActive.current = activeId;
    if (switched) didFirstFit.current = false;   // a switched-to diagram is a first open of ITS layout, so frame it too
    const counts = relCounts(graph.relationships);
    const memberNames = new Set(members.map((t) => t.name));
    // Current positions: keep live drags on a same-diagram reload; a switch resets to the saved layout. Computed
    // outside setNodes so the SAME ordered anchors drive the nodes' handles AND the edges' elbow lanes (edgesFor).
    // While a spread is drawn the flow store holds DRAWN spots, so the saved ones are read from the spread instead.
    const live = switched ? new Map<string, XY>() : held.peek() ? new Map(Object.entries(held.peek()!)) : new Map(rf.getNodes().map((n) => [n.id, n.position as XY]));
    // Carry each card's last measured size into the rebuilt node, so the reflow below compares a toggled card's
    // new size with its old one instead of waiting on every card to be measured from nothing again.
    const measuredOf = new Map(rf.getNodes().map((n) => [n.id, n.measured]));
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
    // A layout reserves each card's REAL box (an open card is taller than its anchor box), so it starts clear.
    const boxOf: SizeOf = (n) => layoutBox(sizeOf(n), measuredOf.get(n));
    const placed = arranging
      ? busMatrixPositions(members, graph.relationships, boxOf, canvasAspect())   // fresh All-tables → default bus matrix
      : placePositions(members, graph.relationships, known, boxOf);     // else keep saved/dragged positions
    // Order each table's anchor slots by the now-known positions (centre = position + half size) — the angle sort.
    const centers = new Map<string, XY>();
    for (const t of members) { const p = placed.get(t.name); if (p) { const s = sizeOf(t.name); centers.set(t.name, { x: p.x + s.w / 2, y: p.y + s.h / 2 }); } }
    const anchors = computeAnchors(members, graph.relationships, effLayout, centers);
    // The cards below are at their saved spots; the spread (effect below) is drawn again from them. A spread still being worked
    // out is stopped here too (round 10): its result was computed for the open cards and the graph of a moment ago, and drawn
    // late it would put the cards on spots no saved layout has (every card closed meanwhile, or a model that changed).
    endSpread();
    if (spreadWorking.current) dropRoom();
    setNodes(members.map((t) => {
      const sz = sizeOf(t.name);
      return {
        id: t.name, type: 'table',
        zIndex: expanded[t.name] ? OPEN_CARD_Z : undefined,
        position: placed.get(t.name) ?? { x: 0, y: 0 },
        measured: measuredOf.get(t.name),
        data: {
          t, rels: counts.get(t.name) ?? 0, curated: !active.all,
          anchors: anchors.get(t.name) ?? EMPTY_ANCHORS, w: sz.w, h: sz.h, layout,
          expanded: !!expanded[t.name],
          columns: expanded[t.name] ? (colsByTable.get(t.name) ?? []) : null,
          related: relatedByTable.get(t.name) ?? EMPTY_SET,
          filter: filters[t.name] ?? '',
          onAddRelated: addRelated, onRemove: removeTable, onToggleExpand, onFilter,
        },
      };
    }));
    setEdges(edgesFor(graph.relationships, memberNames, anchors, centers));
    // Saved positions may predate a card's current size: run the overlap pass once measured. Only for a change of the LAYOUT
    // (review round 8): opening or closing a card rebuilds the cards too, to show or hide its columns, and looking is not a
    // layout change, so it settles nothing, saves nothing and offers nothing.
    const now = { graph, activeId, membershipKey, layout };
    lastSizes.current = sizesToCompare(lastRebuild.current, now, lastSizes.current);   // an arrange placed the cards with their new boxes already
    if (isLayoutChange(lastRebuild.current, now)) {
      settleAll.current = true;
      // Round 9: with cards open the make-room for a reloaded model alone (a link) waits until the spread ends; the others run now.
      if (isArrangeChange(lastRebuild.current, now)) settleNow.current = true;
    }
    lastRebuild.current = now;
    // Persist the default arrangement once so it sticks (a later drag then keeps bus-matrix anchor sides, not free),
    // and frame it (the mount-time fitView fires before the async graph load, so the fresh arrange needs its own fit).
    if (arranging) {
      editActive((d) => ({ ...d, layout: effLayout, positions: { ...d.positions, ...Object.fromEntries(placed) } }));
    }
    // anchors re-sided / columns shown-or-hidden change a node's handle geometry → tell React Flow to re-measure.
    // One call for every card: a call per card is a store update per card, and every line and the bus matrix
    // routing then re-read the whole diagram once per card (an 80-card Expand all froze for seconds).
    window.setTimeout(() => updateNodeInternals(members.map((t) => t.name)), 0);
    // FIRST OPEN: frame the diagram once the graph has actually arrived. The <ReactFlow fitView> prop only fits at
    // initialisation — which happens while `nodes` is still empty, because the graph loads async — so a diagram that
    // opens from SAVED positions (the common case; `arranging` is false whenever a layout was already persisted)
    // never got framed at all and inherited whatever viewport the empty canvas had. Skipped if the person already
    // moved the view while it loaded.
    if (!didFirstFit.current && members.length > 0 && !interactedSinceFit.current) {
      didFirstFit.current = true;
      fitWhenMeasured({ padding: FIT_MARGIN }, true);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [graph, activeId, membershipKey, layout, expandedKey, columns]);

  // A per-node column filter affects ONLY that node's data — patch the single node instead of rebuilding all nodes
  // (which would churn every table + interrupt a hover/drag on each keystroke).
  useEffect(() => {
    setNodes((nds) => nds.map((n) => { const f = filters[n.id] ?? ''; return n.data.filter === f ? n : { ...n, data: { ...n.data, filter: f } }; }));
  }, [filters, setNodes]);

  // Persist the All-tables positions to the engine sidecar (fire-and-forget, best-effort). Custom diagrams stay
  // client-side; only the All-tables layout is engine-owned (the shared, cross-machine, agent-visible one).
  const saveEngineLayout = useCallback((pos: Record<string, XY>, keep = false) => {
    void rpc('saveLayout', positionsToLayoutNodes(pos, keep), ENGINE_LAYOUT_ORIGIN).catch(() => { /* best-effort */ });
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
    updateNodeInternals(members.map((t) => t.name));   // one re-measure for every card, as above
  }, [graph, members, layout, rf, setNodes, setEdges, updateNodeInternals]);

  // Live (position-driven) re-sort while dragging — rAF-throttled to at most one recompute per frame so the attach
  // points + lanes follow the table you're moving without thrashing on every drag event.
  const dragRaf = useRef(0);
  const onNodeDrag = useCallback(() => {
    if (dragRaf.current) return;
    dragRaf.current = requestAnimationFrame(() => { dragRaf.current = 0; restyleFromPositions(); });
  }, [restyleFromPositions]);

  // Store positions in the active diagram (so they survive a reload) + the engine (for All-tables). A drag and a
  // reflow after a card opens or closes both land here, so a moved card persists the same way whichever moved it.
  // Callers pass ONLY the cards that moved: every other table keeps its stored position (UAT-S11-1-004).
  // A card saved this way loses its keep mark (it moved), unless `keep` is set (a put-back), which marks it.
  const persistPositions = useCallback((positions: Record<string, XY>, keep = false) => {
    // Nothing a spread drew is ever saved (round 9): a spot equal to a card's DRAWN spot and not to its saved one is refused.
    let pos = positions;
    const sp = spread.current;
    if (sp) {
      pos = {};
      for (const [id, p] of Object.entries(positions)) {
        const d = sp.drawn[id], v = held.peek()?.[id];
        const isDrawn = d && Math.abs(p.x - d.x) < 0.5 && Math.abs(p.y - d.y) < 0.5, isSaved = v && Math.abs(p.x - v.x) < 0.5 && Math.abs(p.y - v.y) < 0.5;
        if (isDrawn && !isSaved) console.error(`Diagram: refused to save ${id} at a spot only the spread drew`);
        else pos[id] = p;
      }
    }
    if (!Object.keys(pos).length) return;
    editActive((d) => {
      const kept = { ...d.keep };
      for (const id of Object.keys(pos)) { if (keep) kept[id] = true; else delete kept[id]; }
      return { ...d, positions: { ...d.positions, ...pos }, keep: kept };
    });
    if (active.all) saveEngineLayout(pos, keep);
  }, [editActive, active.all, saveEngineLayout]);

  // Making room for the lines (Kane, 28 Sep): when a layout leaves cards on top of each other or too close for the
  // lines between them, the page moves cards apart just enough and saves the moved spots, and the assistant reads them
  // on its next get_layout. One Undo (the toast's, or the row's Put back while the toast is gone) puts back the cards
  // the page last moved and marks them kept, so the page leaves them there from then on, whichever door asks. A kept
  // mark is saved with the layout, so it outlasts a tab switch and a reload and the assistant sees it (get_layout's
  // keep); moving the card, by hand or by save_layout without keep, clears it. Ctrl+Z stays the model's undo (the
  // timeline both doors share, shortcuts.tsx hostCommandForKey): layout is not on that timeline.
  const roomUndo = useRef<{ diagram: string; from: Record<string, XY>; to: Record<string, XY> } | null>(null);
  const [putBack, setPutBack] = useState(0);   // how many cards the live put-back would move, for the row's button
  const notePageMove = useCallback((from: Record<string, XY>, to: Record<string, XY>, more = false) => {
    if (!Object.keys(to).length) return;
    // A further round of the same make-room joins the Undo already offered: one Undo still puts everything back.
    const rec = more && roomUndo.current?.diagram === activeId ? roomUndo.current : { diagram: activeId, from: {}, to: {} };
    for (const id of Object.keys(to)) { if (!(id in rec.from)) rec.from[id] = from[id]; rec.to[id] = to[id]; }
    roomUndo.current = rec;
    const n = Object.keys(rec.to).length;
    setPutBack(n);
    setToast({ text: `Moved ${n} table${n > 1 ? 's' : ''} so the lines have room.`, tone: 'ok', action: { label: 'Undo', run: () => undoRoomRef.current() } });
  }, [activeId]);
  const undoRoom = useCallback(() => {
    if (spread.current) return;   // hidden while spread (round 9); it is offered again when the cards close
    const rec = roomUndo.current;
    roomUndo.current = null; setPutBack(0);
    if (!rec || rec.diagram !== activeId) return;
    // Only a card still where the page left it goes back: one moved since (by the person or their assistant) stays.
    const at = new Map(rf.getNodes().map((n) => [n.id, n.position]));
    const back: Record<string, XY> = {};
    for (const [id, p] of Object.entries(rec.to)) { const q = at.get(id); if (q && Math.abs(q.x - p.x) < 0.5 && Math.abs(q.y - p.y) < 0.5) back[id] = rec.from[id]; }
    if (!Object.keys(back).length) return;
    setNodes((ns) => ns.map((n) => (back[n.id] ? { ...n, position: back[n.id] } : n)));
    persistPositions(back, true);
    // A Put back stays put (review round 7): the settle pass below keeps, where they are, the cards it would still move
    // now these are kept, so a fresh page on this layout moves nothing.
    putBackPass.current = true; settleAll.current = true;
    requestAnimationFrame(() => restyleFromPositions());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeId, rf, setNodes, persistPositions]);
  const undoRoomRef = useRef(undoRoom);
  undoRoomRef.current = undoRoom;
  // Making room runs a few ms at a time (roomSteps, runInSlices) and moves the cards once, when it is done; meanwhile the
  // lines are held (BusRoutes hold), since routing a layout about to change is wasted. A newer change, a drag, another
  // diagram or leaving the Diagram drops it (review round 5: the page made room and routed again up to five times, on
  // the main thread, and froze for 40 s at a time on a 300-table grid).
  const roomStop = useRef<(() => void) | null>(null);
  const [roomBusy, setRoomBusy] = useState(false);
  const dropRoom = useCallback(() => { roomStop.current?.(); roomStop.current = null; spreadWorking.current = false; setRoomBusy(false); }, []);
  useEffect(() => dropRoom, [dropRoom]);
  useEffect(() => { roomUndo.current = null; setPutBack(0); dropRoom(); }, [activeId, dropRoom]);   // a put-back belongs to its own diagram
  // The person's own drag starts something new: the page's last move is no longer the thing an Undo takes back. A
  // make-room it drops starts again once the card is down (its result was worked out without the drag).
  const roomAgain = useRef(false);
  const onNodeDragStart = useCallback(() => { roomUndo.current = null; setPutBack(0); if (roomStop.current) roomAgain.current = true; dropRoom(); }, [dropRoom]);

  // React Flow hands the drop the dragged nodes (one, or a multi-selection); only they are saved.
  const commitPositions = useCallback((_e: unknown, _node: Node, dragged: Node[]) => {
    if (dragRaf.current) { cancelAnimationFrame(dragRaf.current); dragRaf.current = 0; }
    // A card dropped on another, or closer to one than the lines between them need (the routed bus matrix), moves to
    // the nearest clear spot (clearDropped): two stacked cards hide a handle, and its line then runs behind the card on
    // top. Only the dropped cards move, so only they are saved; one Undo puts them where they were dropped.
    // The drop's own spots, not the flow store's: after a quick drag (two pointer moves, or a busy page's coalesced
    // ones) or a Snap step the store still holds the card where it last rendered, clear of everything, while the drag
    // hands over where it was let go (review round 5: such drops were left on other cards and saved there).
    const at = new Map(dragged.map((n) => [n.id, n.position]));
    const all = rf.getNodes().map((n) => (at.has(n.id) ? { ...n, position: at.get(n.id)! } : n));
    // A spot the person can see is preferred: the part of the canvas on screen, in flow px.
    const { width, height, transform: [tx, ty, zoom] } = flowStore.getState();
    const view = width > 0 && height > 0 && zoom > 0 ? { x: -tx / zoom, y: -ty / zoom, w: width / zoom, h: height / zoom } : null;
    const clear = clearDropped(cardsOf(all), dragged.map((n) => n.id), CARD_GAP, snap ? GRID : 0, layout === 'busmatrix' ? linesOf(all) : null, view);
    const sp = spread.current;
    if (sp) {
      // A drag while spread (round 9): the card's SAVED spot moves by the distance it was dragged and only it is saved (its keep
      // mark clears, as any drag does). Round 10 (L1, L2): that spot is then put on the grid when Snap is on and moved to the
      // nearest clear spot of the SAVED layout (the closed boxes), as a drop with every card closed is, so the make-room at the
      // end of the spread has nothing to move: no other card changes because of this drag, and no toast or Put back is offered.
      // It is DRAWN at the nearest clear spot of the spread, and every other card keeps its place in the spread.
      const saved: Record<string, XY> = {}, drawn: Record<string, XY> = {};
      const near = (v: number) => (snap ? Math.round(v / GRID) * GRID : v);
      for (const n of dragged) {
        const from = sp.drawn[n.id], was = held.peek()?.[n.id];
        const to = from && was ? savedAfterDrag(was, from, n.position) : { x: n.position.x, y: n.position.y };
        saved[n.id] = { x: near(to.x), y: near(to.y) };
        drawn[n.id] = clear[n.id] ?? { x: n.position.x, y: n.position.y };
      }
      const closedBoxes = all.map((n) => ({ id: n.id, x: (saved[n.id] ?? held.peek()?.[n.id] ?? n.position).x, y: (saved[n.id] ?? held.peek()?.[n.id] ?? n.position).y, w: (n.data as { w: number }).w, h: (n.data as { h: number }).h }));
      const clearSaved = clearDropped(closedBoxes, dragged.map((n) => n.id), CARD_GAP, snap ? GRID : 0, layout === 'busmatrix' ? linesOf(all) : null, view);
      for (const [id, p] of Object.entries(clearSaved)) saved[id] = p;
      held.amend(saved);
      spread.current = { ...sp, drawn: { ...sp.drawn, ...drawn }, key: sp.done ? sp.key : '' };
      if (Object.keys(clear).length) setNodes((ns) => ns.map((n) => (clear[n.id] ? { ...n, position: clear[n.id] } : n)));
      restyleFromPositions();
      if (Object.keys(clear).length) requestAnimationFrame(() => restyleFromPositions());
      persistPositions(saved);
      roomAgain.current = false;
      return;
    }
    if (Object.keys(clear).length) {
      setNodes((ns) => ns.map((n) => (clear[n.id] ? { ...n, position: clear[n.id] } : n)));
      notePageMove(Object.fromEntries(dragged.filter((n) => clear[n.id]).map((n) => [n.id, { x: n.position.x, y: n.position.y }])), clear);
    }
    restyleFromPositions();   // final crisp re-sort on drop
    if (Object.keys(clear).length) requestAnimationFrame(() => restyleFromPositions());   // and again on the cleared spots
    dragged = dragged.map((n) => (clear[n.id] ? { ...n, position: clear[n.id] } : n));
    persistPositions(nodesToPositions(dragged));
    if (roomAgain.current) { roomAgain.current = false; settleAll.current = true; }   // the re-sort above re-runs the settle pass
  }, [persistPositions, restyleFromPositions, rf, flowStore, setNodes, snap, layout, notePageMove]);

  // Keep cards clear of each other when the LAYOUT changes (Kane, 25 Sep; review rounds 8 and 9). A card that needs a bigger box
  // for its lines moves the cards below it in its column by exactly that change, both ways; any overlap left after that is
  // pushed clear, and the routed bus matrix gets room for its lines (diagramfit.mjs). Looking is not a layout change: opening
  // or closing a card, one or all, saves nothing and offers nothing. The cards slide apart on screen while a card is open (the
  // spread, below); this pass never sees an open card's height, it settles by each card's closed box. `settleAll` asks for the
  // overlap pass after a rebuild or an arrange even when no box changed, since saved positions may predate a card's box.
  const lastSizes = useRef(new Map<string, FitSize>());
  const settleAll = useRef(false);
  const putBackPass = useRef(false);   // the next settle pass follows a Put back: it keeps cards instead of moving them
  // Moves the cards to `next`, saves the ones that moved from `from` (where they were drawn) and offers the page's own
  // moves (`page`: the positions they are measured from) to one Undo.
  const land = (next: Record<string, XY>, from: Record<string, XY>, page: Record<string, XY>, more = false) => {
    const moved = changedPositions(from, next);
    if (!Object.keys(moved).length) return;
    setNodes((ns) => ns.map((n) => (moved[n.id] ? { ...n, position: moved[n.id] } : n)));
    persistPositions(changedPositions(from, next));   // the cards the reflow or the room moved, no others
    const pageMoved = changedPositions(page, next);
    notePageMove(Object.fromEntries(Object.keys(pageMoved).map((id) => [id, page[id]])), pageMoved, more);
    requestAnimationFrame(() => restyleFromPositions());   // the lines re-sort their attach points on the new spots
  };
  // The links roomSteps takes, from where the cards stand (their ends re-sort on the centers).
  const linksOf = (boxes: { id: string; x: number; y: number; w: number; h: number }[]) => {
    const centers = new Map(boxes.map((b) => [b.id, { x: b.x + b.w / 2, y: b.y + b.h / 2 }]));
    const ends = new Map<string, { id: string; side: string; frac: number }>();
    for (const [table, list] of computeAnchors(members, graph!.relationships, layout, centers)) for (const a of list) ends.set(a.rel + ':' + a.kind, { id: table, side: a.side, frac: a.frac });
    return graph!.relationships.flatMap((r) => { const from = ends.get(r.name + ':source'), to = ends.get(r.name + ':target'); return from && to ? [{ id: r.name, from, to }] : []; });
  };
  const spreadAgain = () => setSpreadTick((t) => t + 1);
  useEffect(() => {
    if (!nodes.length || nodes.some((n) => !n.measured?.width || !n.measured?.height)) return;
    if (nodes.some((n) => n.data.layout !== layout)) return;   // an arrange moved the cards before the rebuild gave them the new layout's boxes: settle then
    // Round 9: while a card is open the page draws a spread instead (the effect below), and this make-room waits, with `lastSizes`
    // and `settleAll` as they are, until the last card closes. Only a change that asks to run now (settleNow) goes ahead.
    if (!settleNow.current && nodes.some((n) => n.data.expanded)) return;
    // The cards are settled by their CLOSED boxes, whether they are open or not (review round 8: looking is not a layout
    // change). An opened card's measured size would turn every look into growth; the spread draws that growth, unsaved.
    const cur = new Map(nodes.map((n) => [n.id, { w: n.data.w, h: n.data.h }]));
    const { prev, changed } = boxChanges(nodes.map((n) => ({ id: n.id, ...cur.get(n.id)! })), lastSizes.current);
    lastSizes.current = cur;
    if (!changed && !settleAll.current) { if (settleNow.current) { settleNow.current = false; spreadAgain(); } return; }
    settleAll.current = false; settleNow.current = false;
    dropRoom();   // this change replaces a make-room still working
    const keep = new Set(Object.keys(active.keep ?? {}));
    const afterPutBack = putBackPass.current && !changed;   // a card opened in the same beat is an ordinary pass
    putBackPass.current = false;
    if (afterPutBack) {
      // Nothing moves and nothing is offered: the cards the pass would move are kept where they are (saved with the keep
      // mark, so the assistant reads them too) until a fresh page has nothing to move. Lines are held meanwhile.
      const at = nodesToPositions(nodes);
      let finished = false;
      const finish = (added: Set<string>) => {
        finished = true; roomStop.current = null; setRoomBusy(false);
        if (added.size) persistPositions(Object.fromEntries([...added].map((id) => [id, at[id]])), true);
        spreadAgain();
      };
      const cards = nodes.map((n) => ({ id: n.id, x: n.position.x, y: n.position.y, ...cur.get(n.id)! }));
      if (layout !== 'busmatrix' || !graph) {   // only the overlap pass runs here
        const grownOnly = reflowCards(cards, prev, CARD_GAP, keep);
        finish(new Set(Object.keys(changedPositions(at, grownOnly)).filter((id) => !keep.has(id))));
        return;
      }
      const stop = runInSlices(keepBeside(cards, linksOf, linesOf(nodes), snap ? GRID : 0, keep), finish);
      if (!finished) { roomStop.current = stop; setRoomBusy(true); }
      return;
    }
    const grown = reflowCards(nodes.map((n) => ({ id: n.id, x: n.position.x, y: n.position.y, ...cur.get(n.id)! })), prev, CARD_GAP, keep);
    const before = nodesToPositions(nodes);
    // What an Undo puts back is the page's own moves, not a card following the one that grew above it (closing that
    // card moves it back). A pass with no size change moved every card it moved to make room, so all of those count.
    const page = changed ? grown : before;
    if (layout !== 'busmatrix' || !graph) { land(grown, before, page); spreadAgain(); return; }
    // Then room for the lines (the routed bus matrix only): no card on another, and every gap wide enough for its lines.
    const boxes = nodes.map((n) => ({ id: n.id, ...grown[n.id], ...cur.get(n.id)! }));
    const links = linksOf(boxes);
    let now: Record<string, XY> | null = null, later: ((room: Record<string, XY>) => void) | null = null;
    const stop = runInSlices(roomSteps(boxes, links, linesOf(nodes), snap ? GRID : 0, keep), (room) => (later ? later(room) : (now = room)));
    if (now) { land({ ...grown, ...(now as Record<string, XY>) }, before, page); spreadAgain(); return; }   // done in its first slice
    // Still working: a size change moves its cards now; the room lands when it is done, joining the same Undo.
    roomStop.current = stop; setRoomBusy(true);
    if (changed) land(grown, before, page);
    const from = changed ? grown : before;
    later = (room) => { roomStop.current = null; setRoomBusy(false); land({ ...grown, ...room }, from, page, changed); spreadAgain(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [nodes]);

  // The spread (Kane, 29 Sep, "Slide down"). While any card is open the cards are DRAWN where the layout would put them to make
  // space for the open ones: the cards below an open card move down by its growth, then apart until no card covers another and
  // the lines have room (the pass the page ran before round 8: spreadStart, then roomSteps). It is worked out from the SAVED
  // layout plus the set of open cards, so the same open cards always draw the same spread, and it is a view only: nothing is
  // saved, toasted, offered as a Put back or marked kept, and closing the last card puts every card back on its saved spot
  // (the rebuild that closes it reads the saved spots, never the drawn ones). A layout change that happens meanwhile (a link)
  // has its make-room run once, when the spread ends.
  const spreadCache = useRef(new Map<string, Record<string, XY>>());
  useLayoutEffect(() => {
    if (!graph || !nodes.length || nodes.some((n) => !n.measured?.width || !n.measured?.height || n.dragging)) return;
    if (!nodes.some((n) => n.data.expanded)) {
      // Nothing is open: there is no spread. One still being worked out is stopped, or its result is drawn over the saved layout
      // when it lands (round 10, R9-1: every card left on the spread's spots, and the next save stored them). Any card still on a
      // spot the spread drew goes back to its saved spot (round 11: the store is never left on the drawn spots with no card open).
      const was = endSpread();
      if (spreadWorking.current) dropRoom();
      if (was) setNodes((ns) => ns.map((n) => (was[n.id] ? { ...n, position: was[n.id] } : n)));
      return;
    }
    if (settleNow.current) return;   // the make-room that asked to run now goes first, then the spread is drawn from what it saved
    if (roomStop.current && !spreadWorking.current) return;   // a make-room is working; its end asks for the spread again
    const s = spread.current;
    const keep = new Set(Object.keys(active.keep ?? {}));
    const sizes = nodes.map((n) => ({ id: n.id, w: n.measured!.width!, h: n.measured!.height!, open: !!n.data.expanded }));
    // What the picture depends on: the open cards and the sizes they are drawn at, and the layout mode. Not Snap (the spread is a
    // view, so it keeps no grid) and not the kept cards (a drag while spread clears one, which must not redraw the others).
    const key = spreadKey(sizes, layout);
    if (s && s.key === key) return;   // this picture is already drawn (or being worked out)
    const savedNow = held.peek() ?? nodesToPositions(nodes);
    dropRoom();   // replaces a spread still being worked out
    const draw = (to: Record<string, XY>, done: boolean) => {
      // The open set can change while the work runs; a spread is only ever drawn for cards that are open now. Nothing is cleared
      // here: the saved spots are `held`, and the rebuild that closed the cards puts every card back on them.
      if (!anyOpenRef.current) return;
      held.hold(savedNow);
      spread.current = { drawn: to, key, done };
      setNodes((ns) => ns.map((n) => { const p = to[n.id]; return p && (Math.abs(p.x - n.position.x) > 0.001 || Math.abs(p.y - n.position.y) > 0.001) ? { ...n, position: p } : n; }));
      requestAnimationFrame(() => restyleFromPositions());   // the lines re-sort their attach points on the drawn spots
    };
    const { needed, grown } = spreadStart(nodes.map((n, i) => ({ id: n.id, x: (savedNow[n.id] ?? n.position).x, y: (savedNow[n.id] ?? n.position).y, w: sizes[i].w, h: sizes[i].h, bw: n.data.w, bh: n.data.h, open: sizes[i].open })), keep);
    if (!needed) {   // nothing is drawn bigger than its closed box: the saved spots are what is drawn
      const was = endSpread();
      if (was) setNodes((ns) => ns.map((n) => (was[n.id] ? { ...n, position: was[n.id] } : n)));
      return;
    }
    const memo = key + '#' + [...keep].sort().join(',') + '#' + JSON.stringify(Object.values(savedNow).map((p) => [Math.round(p.x * 10), Math.round(p.y * 10)]));
    const hit = spreadCache.current.get(memo);
    if (hit) { draw(hit, true); return; }
    const done = (to: Record<string, XY>) => {
      spreadCache.current.set(memo, to);
      if (spreadCache.current.size > 8) spreadCache.current.delete(spreadCache.current.keys().next().value!);
      draw(to, true);
    };
    if (layout !== 'busmatrix') { done(grown); return; }
    const boxes = nodes.map((n, i) => ({ id: n.id, ...grown[n.id], w: sizes[i].w, h: sizes[i].h }));
    let now: Record<string, XY> | null = null, later: ((room: Record<string, XY>) => void) | null = null;
    const stop = runInSlices(roomSteps(boxes, linksOf(boxes), linesOf(nodes), 0, keep), (room) => (later ? later(room) : (now = room)));
    if (now) { done({ ...grown, ...(now as Record<string, XY>) }); return; }   // done in its first slice
    // Still working: the cards below an open one slide down now, and the room for the lines lands when it is done.
    roomStop.current = stop; spreadWorking.current = true; setRoomBusy(true);
    draw(grown, false);
    later = (room) => { roomStop.current = null; spreadWorking.current = false; setRoomBusy(false); done({ ...grown, ...room }); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [nodes, spreadTick]);

  // Apply a layout function to the shown tables, record the layout mode (so handle sides re-side), persist
  // positions, and fit the view. Open cards stay open: the layout reserves each card's measured box instead.
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
    const sizeOf: SizeOf = (n) => layoutBox(sizeForAnchors(anchors.get(n) ?? EMPTY_ANCHORS), rf.getInternalNode(n)?.measured, !!expanded[n]);
    const placed = layoutFn(baseMembers, graph.relationships, sizeOf);
    // Round 9: an arrange ends the spread first (every card back on its saved spot); the spread is drawn again from the new
    // layout for the cards still open, once the make-room below has run.
    const was = endSpread(); dropRoom();
    setNodes((ns) => ns.map((n) => ({ ...n, position: placed.get(n.id) ?? was?.[n.id] ?? n.position })));
    editActive((d) => ({ ...d, positions: Object.fromEntries(placed), layout: mode }));
    settleAll.current = true; settleNow.current = true;
    if (active.all) saveEngineLayout(Object.fromEntries(placed));
    // A layout change is an explicit command ("show me the bus matrix"), so this fit is unconditional — the
    // interaction guard exists to protect a view the person chose, not to suppress the arrangement they just asked for.
    // It frames READABLY, as the first open does: a full fit after Vertical or Layered drew the text at about 4px in a
    // narrow Studio (journey m11-2, F17). Fit on the row still shows the whole diagram.
    fitWhenMeasured({ padding: FIT_MARGIN, duration: 300 }, true);
  }, [graph, baseMembers, setNodes, editActive, rf, active.all, saveEngineLayout, fitWhenMeasured, dropRoom, expanded]);
  const autoArrange = useCallback(() => arrangeWith(hierarchyPositions, 'hierarchy'), [arrangeWith]);
  const busMatrix = useCallback(() => arrangeWith((m, r, sz) => busMatrixPositions(m, r, sz, canvasAspect()), 'busmatrix'), [arrangeWith, canvasAspect]);
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
    if (addReq && addReq.nonce > consumedAddNonce) {
      consumedAddNonce = addReq.nonce; addTablesToActive(addReq.tables); onAddConsumed?.(addReq.nonce);
    }
  }, [addReq, addTablesToActive]);
  useEffect(() => {
    if (!focusReq || !graph?.relationships.some((relationship) => relationship.name === focusReq.id)) return;
    setFocusedEdge(focusReq.id); setSelectedRel(focusReq.id);
  }, [focusReq?.nonce, graph]);

  // --- create / edit relationships ---
  const onConnect = useCallback(async (c: Connection) => {
    const colName = (h?: string | null) => (h && h.startsWith('col:') ? h.slice(4, h.lastIndexOf(':')) : null);
    const s = colName(c.sourceHandle), t = colName(c.targetHandle);
    if (!c.source || !c.target || !s || !t) return;   // ignore drags that aren't column→column (e.g. routing anchors)
    if (c.source === c.target) { setToast({ text: 'A relationship needs two different tables.', tone: 'error' }); return; }
    const srcRef = `column:${c.source}/${s}`, tgtRef = `column:${c.target}/${t}`;
    const srcType = colByRef.get(srcRef)?.dataType, tgtType = colByRef.get(tgtRef)?.dataType;
    if (srcType && tgtType && srcType !== tgtType) {
      setToast({ text: `A relationship needs two columns of the same type. ${s} is ${plainRelType(srcType)} and ${t} is ${plainRelType(tgtType)}.`, tone: 'error' });
      return;
    }
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
        setToast({ text: 'An active relationship already exists between those tables.', tone: 'error', action: { label: 'Add inactive', run: async () => {
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
  // While a card is open the Put back is hidden (round 9: the cards drawn are a spread, not the layout the Put back is about), and
  // so is the make-room toast's Undo; the row's Put back comes back when the cards close.
  const anyOpen = members.some((t) => expanded[t.name]);
  anyOpenRef.current = anyOpen;
  useEffect(() => { if (anyOpen && toast?.action?.label === 'Undo') setToast(null); }, [anyOpen, toast]);

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

  // The row measures ITSELF, not the window: a narrower editor column has to fold just as a narrow window does.
  // Two folds, in priority order. Show goes first because a filter you set once is the least-reached-for
  // group on the row; the arrange cluster goes second. Snap and Fit never fold: Fit is what a person reaches
  // for the moment a resize leaves the diagram looking wrong.
  const row = useToolRow(2);
  // The canvas is a flex child with no height of its own to read, and the second chip's fate depends on that
  // height, so the box is measured directly - the VISIBLE box, because the page frame lets the canvas run a
  // little way behind the bottom bar and a chip pinned to the hidden strip is a chip nobody sees.
  const [canvasRef, canvasBox] = useCanvasBox();
  const canvasH = canvasBox.height;
  const legendText = 'Line colours: green is a one-way link, amber filters both ways, grey dashed is switched off. The fork end of a line is the many side, the bar end is the one side.';
  const arrangeControls = (
    <>
      <button className="sem-btn sem-btn-sm" onClick={autoArrange} aria-pressed={layout === 'hierarchy'} title="Vertical: facts on the left, lookup tables on the right. Tables stack down each band and the links run across as lanes">Vertical</button>
      <button className="sem-btn sem-btn-sm" onClick={layered} aria-pressed={layout === 'layered'} title="Layered: lookup tables across the top, facts underneath. Every link is a clean straight lane">Layered</button>
      <button className="sem-btn sem-btn-sm" onClick={busMatrix} aria-pressed={layout === 'busmatrix'} title="Bus matrix, the usual one: facts down the left, lookup tables across the top, unlinked tables on the right">Bus matrix</button>
      <button className="sem-btn sem-btn-sm" onClick={() => setExpandAll(true)} title="Open every table so you can see its columns and drag one column onto another to link them">Expand all</button>
      <button className="sem-btn sem-btn-sm" onClick={() => setExpandAll(false)} title="Close every table">Collapse all</button>
      <button className="sem-btn sem-btn-sm" onClick={() => setSnap((s) => !s)} aria-pressed={snap} title="Line dragged tables up on a grid, so a layout you arrange by hand stays tidy">Snap to grid</button>
    </>
  );
  const kindChips = (
    <>
      <KindChip on={showData} onClick={() => setShowData((v) => !v)} color="var(--sem-accent)" label="Tables" count={kindCounts.data} title="Show regular data tables" />
      <KindChip on={showCalc} onClick={() => setShowCalc((v) => !v)} color="#17B3A3" label="Calculated" count={kindCounts.calc} title="Show tables built from a calculation" />
      <KindChip on={showFp} onClick={() => setShowFp((v) => !v)} color={FIELD_PARAM_VIOLET} label="Field params" count={kindCounts.fp} title="Show field parameters" />
    </>
  );

  if (err) return <div className="p-4"><div className="rounded-(--sem-radius-card) px-3 py-2 text-xs" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{err}</div></div>;
  if (!graph) return <div className="p-6 text-xs" style={{ color: 'var(--sem-muted)' }}>Loading model graph…</div>;

  return (
    <div className="h-full flex flex-col">
      {/* ONE tool row. The hint sentence that used to sit beside the Canvas / Relationships switch is now the
          tooltip of ＋ New and a line in DIAGRAM_NOTES; the counts and the legend are chips inside the canvas. */}
      <ToolRow rowRef={row.ref}>
        {modeSwitch}
        <RowSep />
        {renaming ? (
          <input autoFocus value={nameDraft} onChange={(e) => setNameDraft(e.target.value)} aria-label="Diagram name"
            onBlur={commitRename} onKeyDown={(e) => { if (e.key === 'Enter') commitRename(); if (e.key === 'Escape') setRenaming(false); }}
            className="sem-toolrow-input" style={{ maxWidth: 180 }} />
        ) : (
          <select value={activeId} onChange={(e) => setActiveId(e.target.value)} className="sem-toolrow-input" style={{ maxWidth: 180 }} title="Switch diagram">
            {diagrams.map((d) => <option key={d.id} value={d.id}>{d.name}{d.all ? '' : ` (${graph.tables.filter((t) => d.tables.includes(t.name)).length})`}</option>)}
          </select>
        )}
        <button className="sem-btn sem-btn-sm" onClick={newDiagram} aria-label="New diagram" title={DIAGRAM_CANVAS_HINT}>＋ New</button>
        {/* Rename and delete are rare and destructive, so they fold behind one control rather than each taking
            room on every screen from the controls people use all day. */}
        {!active.all && (
          <RowMenu label="⋯" title="Rename or delete this diagram">
            <button className="sem-btn sem-btn-sm" onClick={() => { setNameDraft(active.name); setRenaming(true); }}>Rename</button>
            <button className="sem-btn sem-btn-sm" onClick={deleteDiagram}>Delete this diagram</button>
          </RowMenu>
        )}
        {!active.all && (
          <select value="" onChange={(e) => { if (e.target.value) addTables([e.target.value]); }} className="sem-toolrow-input" style={{ maxWidth: 150 }} title="Add a table to this diagram" disabled={absent.length === 0}>
            <option value="">＋ Add table…</option>
            {absent.map((n) => <option key={n} value={n}>{n}</option>)}
          </select>
        )}
        <RowSep />
        {/* Always one menu: five layout buttons and Snap said the same thing the canvas already shows. Fit is the zoom
            stack's own button at the canvas corner. */}
        <RowMenu label="Arrange" closeOnPick title="Lay the tables out, open or close them all, or snap to a grid">{arrangeControls}</RowMenu>
        {putBack > 0 && !anyOpen && <button className="sem-btn sem-btn-sm" onClick={() => undoRoomRef.current()} title={`Put the ${putBack} table${putBack > 1 ? 's' : ''} the Diagram just moved back where ${putBack > 1 ? 'they were' : 'it was'}, and leave ${putBack > 1 ? 'them' : 'it'} there`}>Put back</button>}
        <RowSep />
        <RowMenu label="Show" title="Choose which kinds of table to show" align="end" badge={[showData, showCalc, showFp].filter((v) => !v).length}>{kindChips}</RowMenu>
      </ToolRow>
      <CardinalityMarkers />
      {/* The canvas is ALWAYS mounted (even when empty) so it's a live drop target for tables dragged from the Model
          tree; the empty-state hint is an overlay on top (pointer-events:none so it never blocks a drop). */}
      <div className="flex-1 min-h-0 relative xe-canvas" ref={canvasRef} style={canvasInsetStyle(canvasBox.bottomInset)} onDragOver={onCanvasDragOver} onDrop={onCanvasDrop}
        data-making-room={roomBusy ? 'true' : undefined /* while the cards' new spots are worked out: nothing on screen shows it */}>
        {!active.all && absent.length > 0 && <TablePalette tables={absent} onAdd={(n) => addTables([n])} />}
        {selRel && <RelPropsPanel rel={selRel} onClose={() => setSelectedRel(null)} onChanged={load} onDelete={deleteRelationship} />}
        {toast && (
          <div className="absolute left-1/2 -translate-x-1/2 bottom-3 z-10 flex items-center gap-2 rounded-(--sem-radius-control) border px-3 py-1 text-(length:--sem-text-label) shadow-lg"
            style={{ background: 'var(--sem-surface)', borderColor: toast.tone === 'ok' ? 'var(--sem-good)' : 'var(--sem-bad)', color: 'var(--sem-fg)' }}>
            <span>{toast.text}</span>
            {toast.action && <button onClick={() => { const a = toast.action!; setToast(null); void a.run(); }} style={tbBtn}>{toast.action.label}</button>}
            <button onClick={() => setToast(null)} style={{ background: 'transparent', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer' }}>✕</button>
          </div>
        )}
        {/* Bus matrix lines route round the cards, all in one pass per geometry change; each takes its own track. */}
        <BusRoutes on={layout === 'busmatrix'} hold={roomBusy}>
        <ReactFlow className="xe-flow"
          nodes={displayNodes} edges={displayEdges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
          onNodesChange={onNodesChange} onNodeDragStart={onNodeDragStart} onNodeDrag={onNodeDrag} onNodeDragStop={commitPositions}
          onConnect={onConnect} onEdgeClick={onEdgeClick} onEdgeDoubleClick={onEdgeDoubleClick} onPaneClick={clearFocus}
          onMove={(evt) => { if (evt) interactedSinceFit.current = true; }}   // evt is null for our own programmatic fitView, non-null for a real drag/wheel
          connectionMode={ConnectionMode.Loose} connectionLineComponent={RelConnectionLine}
          snapToGrid={snap} snapGrid={[GRID, GRID]}
          fitView minZoom={MIN_ZOOM} proOptions={{ hideAttribution: true }}
        >
          <Background color="var(--sem-border)" gap={20} />
          <Controls showInteractive={false} />
        </ReactFlow>
        </BusRoutes>
        {/* The counts and the legend, which used to be a 34px row of their own above the canvas. As chips they
            cost the canvas nothing, and they sit clear of the zoom controls React Flow pins to the bottom-left.
            On a canvas too short for two chips the legend folds into the counts chip's tooltip rather than
            stacking a second chip over the picture. */}
        {audit && (
          // The Add tables list already owns the canvas's left edge on a custom diagram, so the chip starts to
          // the right of it rather than sitting underneath it.
          <CanvasChip at="start" style={!active.all && absent.length > 0 ? { left: 200 } : undefined}
            title={canvasH >= CANVAS_CHIP_MIN_H ? undefined : legendText}>
            <span><b>{audit.tables}</b> tables</span>
            <span><b>{audit.rels}</b> links</span>
            {audit.bidi > 0 && <span><b>{audit.bidi}</b> filter both ways</span>}
            {audit.inactive > 0 && <span><b>{audit.inactive}</b> switched off</span>}
            {audit.isolated > 0 && <span><b>{audit.isolated}</b> not linked</span>}
            {members.length !== audit.tables && <span>{members.length} of {audit.tables} shown</span>}
          </CanvasChip>
        )}
        {audit && canvasH >= CANVAS_CHIP_MIN_H && (
          <CanvasChip at="end" title={legendText}>
            <CardGlyph kind="many" /> many
            <CardGlyph kind="one" /> one
            <span style={{ width: 1, height: 12, background: 'var(--sem-border)' }} />
            <Legend color="var(--sem-accent)" text="1-way" />
            <Legend color="var(--sem-warn)" text="both ways" />
            <Legend color="var(--sem-muted)" text="switched off" dashed />
          </CanvasChip>
        )}
        {members.length === 0 && (
          <div className="absolute inset-0 flex items-center justify-center text-xs pointer-events-none px-6 text-center" style={{ color: 'var(--sem-muted)' }}>
            {baseMembers.length > 0
              ? 'Every table is hidden by a filter. Switch one back on under Show.'
              : 'This diagram is empty. Drag a table from the Add tables list onto the canvas, pick one under ＋ Add table, or select a table and choose ＋ related.'}
          </div>
        )}
      </div>
    </div>
  );
}

const tbInput: React.CSSProperties = {
  background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)',
  border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', fontSize: 'var(--sem-text-label)', padding: 'var(--sem-space-1) var(--sem-space-2)', maxWidth: 220,
};

// The Diagram tab hosts two views of the model's relationships: the interactive ER Canvas and a sortable
// Relationships grid (relocated here when the old "Audit" tab was retired). A segmented toggle switches between
// them; the choice persists. Both read the same live model graph, so they stay in sync.
// `addTables` is a one-shot request from the host (Model-tree "Add to Studio Diagram") carrying the table(s) to drop
// onto the canvas, with a nonce so a repeat add re-fires. Forwarded to DiagramInner, which consumes it once.
export function DiagramView({ addTables, focusRelationship, onNavConsumed }: {
  addTables?: { tables: string[]; nonce: number } | null;
  focusRelationship?: { id: string; nonce: number } | null;
  onNavConsumed?: (nonce: number) => void;
}) {
  const [view, setView] = useState<'canvas' | 'rels'>(() => loadState<'canvas' | 'rels'>('diagramView', 'canvas'));
  useEffect(() => { saveState('diagramView', view); }, [view]);
  useEffect(() => { if (focusRelationship) setView('canvas'); }, [focusRelationship?.nonce]);
  // The switch is built HERE (it owns the view state) and handed to whichever view is showing, so the pair leads
  // that view's single tool row instead of standing on a row of its own above it.
  const modeSwitch = (
    <div className="sem-seg" role="group" aria-label="Diagram view">
      <button className="sem-seg-item" aria-pressed={view === 'canvas'} onClick={() => setView('canvas')} title={DIAGRAM_CANVAS_HINT}>Canvas</button>
      <button className="sem-seg-item" aria-pressed={view === 'rels'} onClick={() => setView('rels')} title={DIAGRAM_RELATIONSHIPS_HINT}>Relationships</button>
    </div>
  );
  return (
    <div className="h-full flex flex-col">
      {view === 'canvas'
        ? <ReactFlowProvider><DiagramInner addReq={addTables ?? null} focusReq={focusRelationship} onAddConsumed={onNavConsumed} modeSwitch={modeSwitch} /></ReactFlowProvider>
        : <RelationshipsView modeSwitch={modeSwitch} />}
    </div>
  );
}

// A compact table-kind toggle on the diagram's tool row. The swatch matches that kind's node accent (green data,
// teal calc, violet field-parameter) so the chip reads as "show these". ON = filled swatch + pressed button;
// OFF = hollow swatch + dimmed. Toggling hides the whole kind (and its lines) from the canvas. It takes the shared
// dense control scale, so it is the same height as every other control on the row.
function KindChip({ on, onClick, color, label, count, title }: { on: boolean; onClick: () => void; color: string; label: string; count: number; title: string }) {
  return (
    <button className="sem-btn sem-btn-sm" onClick={onClick} title={title} aria-pressed={on} style={{ opacity: on ? 1 : 0.6 }}>
      <span style={{ width: 8, height: 8, borderRadius: 'var(--sem-radius-chip)', background: on ? color : 'transparent', border: `1px solid ${color}` }} />
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
    <div className="absolute top-2 right-2 z-10 rounded-(--sem-radius-card) border shadow-lg text-(length:--sem-text-label)" style={{ width: 264, background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2 px-3 py-2 border-b" style={{ borderColor: 'var(--sem-border)' }}>
        <span className="font-semibold">Relationship</span>
        <button className="ml-auto" onClick={onClose} style={tbBtn} title="Close (Esc)">✕</button>
      </div>
      <div className="px-3 py-2 flex flex-col gap-2">
        <div className="font-mono" style={{ color: 'var(--sem-muted)', fontSize: 'var(--sem-text-label)' }}>{rel.fromTable}[{rel.fromColumn}] → {rel.toTable}[{rel.toColumn}]</div>
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
          style={{ ...tbBtn, color: 'var(--sem-bad)', borderColor: 'var(--sem-bad)', marginTop: 'var(--sem-space-1)' }}>Delete relationship</button>
        {error && <div className="xe-dot xe-dot-bad" style={{ fontSize: 'var(--sem-text-label)' }}>{error}</div>}
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
    <div className="absolute top-2 left-2 z-10 rounded-(--sem-radius-card) border shadow-lg text-(length:--sem-text-label) flex flex-col" style={{ width: 184, maxHeight: 'calc(100% - 16px)', background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-2 px-3 py-1"
        style={{ borderBottom: open ? '1px solid var(--sem-border)' : 'none', background: 'transparent', color: 'var(--sem-fg)', cursor: 'pointer' }} title="Drag a table onto the canvas to add it">
        <span style={{ fontSize: 'var(--sem-text-label)', width: 10 }}>{open ? '▾' : '▸'}</span>
        <span className="font-semibold">Add tables</span>
        <span className="ml-auto" style={{ color: 'var(--sem-muted)' }}>{tables.length}</span>
      </button>
      {open && (
        <>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="filter tables…"
            style={{ ...tbInput, margin: 'var(--sem-space-2) var(--sem-space-2) var(--sem-space-1)', maxWidth: 'unset' }} />
          <div className="nowheel" style={{ overflowY: 'auto', overflowX: 'hidden', padding: '0 var(--sem-space-2) var(--sem-space-2)' }}>
            {shown.map((t) => (
              <div key={t} draggable onDragStart={(e) => { e.dataTransfer.setData('application/x-semanticus-table', t); e.dataTransfer.effectAllowed = 'copy'; }}
                onDoubleClick={() => onAdd(t)} title="Drag onto the canvas (or double-click to add)" className="truncate flex items-center gap-1"
                style={{ padding: 'var(--sem-space-1) var(--sem-space-2)', marginBottom: 'var(--sem-space-1)', borderRadius: 'var(--sem-radius-card)', cursor: 'grab', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>
                <span aria-hidden style={{ color: 'var(--sem-muted)', fontSize: 'var(--sem-text-label)' }}>⠿</span><span className="truncate">{t}</span>
              </div>
            ))}
            {shown.length === 0 && <div style={{ color: 'var(--sem-muted)', padding: 'var(--sem-space-1) var(--sem-space-1)' }}>no matches</div>}
          </div>
        </>
      )}
    </div>
  );
}
function PropRow({ label, children }: { label: string; children: React.ReactNode }) {
  return <div className="flex items-center gap-2"><span className="w-20 shrink-0" style={{ color: 'var(--sem-muted)' }}>{label}</span>{children}</div>;
}
