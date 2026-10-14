import { createContext, useContext, useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { BaseEdge, getSmoothStepPath, getBezierPath, Position, useInternalNode, useStore, ViewportPortal, type EdgeProps, type ConnectionLineComponentProps, type ReactFlowState } from '@xyflow/react';
import { routeAll, routeSteps, holdRoutes, pathOf, type RouteBox, type RouteEnd, type RouteResult } from './diagramroute.mjs';
import { MATRIX_LANE, nearMarkPoints, tagPoint } from './diagramlayout.mjs';
import { RelationshipSnap } from './personality/motion';
import { useRelationshipMotion } from './personality/motion/relationships';

// Custom relationship edge. ORTHOGONAL (right-angle) routing — the ER-diagram look. A line is a simple Z: two
// straight runs out of the two handles + ONE crossbar between them. For a VERTICAL edge (the layered view: fact-top
// ↔ dim-bottom) we place that single crossbar at a sequence-staggered height (`data.lane` ∈ (0,1), interpolated
// between the two handles), so each dim's vertical-segment length differs and the crossbars never overlap — and
// `offset:0` keeps it to exactly one crossbar (no extra jogs). Other edges keep the small per-edge `offset` stagger.
// A self-loop arcs via a bezier (a step path would be degenerate). Markers + line style pass straight through.
// The OUTWARD direction (degrees) a line leaves a node, by the handle side it sits on — used to rotate the
// cardinality marker so it always follows the line, in any layout (the SVG marker `orient="auto"` did NOT rotate
// reliably for vertical edges, which is why we draw the markers ourselves here).
const OUTWARD: Record<string, number> = { top: -90, bottom: 90, left: 180, right: 0 };
// One cardinality marker at a line END, drawn in LOCAL coords (+x = outward, away from the node) then translated to
// the handle and rotated to the line: "many" = a crow's-foot fork (point outward toward the other entity, three prongs
// fanning back to this node's edge); "one" = a short perpendicular bar just off the node.
function endMarker(x: number, y: number, pos: Position, many: boolean | undefined, color: string, key: string) {
  const deg = OUTWARD[pos as string] ?? 0;
  const d = many ? 'M9,0 L0,-5 M9,0 L0,5' : 'M7,-4.5 L7,4.5';
  return <path key={key} d={d} transform={`translate(${x},${y}) rotate(${deg})`} style={{ stroke: color, strokeWidth: 1.4, fill: 'none' }} />;
}

// --- Bus matrix routing: ONE pass for every line, not one search per line per render (m11/diagram-route D1) ---
// Cards can sit anywhere after a drag, so each line is routed through the gaps between cards (diagramroute.mjs) and
// never runs behind one. Routing every line together is also what gives each its own track (routeAll). The pass runs
// only when the geometry changes: a card moved or resized, or a line's end moved. A pan, a zoom, a hover or a click
// on a line changes none of that and costs nothing. While a card is being dragged, lines whose ends did not move and
// that still clear every card keep their routes, so a drag frame routes only the lines it disturbs; the drop routes
// the whole diagram once more. Any other change (opening the Diagram, cards measured, a card opened and the cards
// below it moved) comes in a burst of many small changes, so the pass waits until the burst settles and runs once:
// routing on every step of that burst is what froze an 80-table model for seconds. Until it runs, a line whose ends
// moved keeps its last route, moved onto its new ends and drawn faded (holdRoutes): leaving those lines out made every
// line vanish for a second or more after Expand all on a big model (review round 3). The held line can cross a card
// that just grew, for as long as the burst lasts; the settled pass then routes it round.
// The settled pass runs a few ms at a time (routeSteps), so the page keeps answering while it works: one pass of an
// assistant's 300-table grid took 30 s, and ran in one piece on the main thread (review round 5). A change while it
// runs drops it and starts again from the new geometry. `hold` keeps the lines held and routes nothing, while the
// Diagram is still working out where the cards go (making room): routing a layout that is about to change is wasted.

/** The routes of the current bus matrix by edge id, or null when the layout is not the bus matrix. */
export const RouteContext = createContext<Map<string, RouteResult> | null>(null);

type Bounds = { id?: string | null; x: number; y: number; width: number; height: number; position: Position };
// Where React Flow draws a line's end: the same arithmetic as its getHandlePosition (@xyflow/system), which the
// package does not export, so a route starts exactly on the point SemRelEdge is handed as sourceX/sourceY.
function handleEnd(n: { internals: { positionAbsolute: { x: number; y: number } } }, h: Bounds): RouteEnd {
  const x = h.x + n.internals.positionAbsolute.x, y = h.y + n.internals.positionAbsolute.y;
  switch (h.position) {
    case Position.Top: return { x: x + h.width / 2, y, side: 'top' };
    case Position.Right: return { x: x + h.width, y: y + h.height / 2, side: 'right' };
    case Position.Bottom: return { x: x + h.width / 2, y: y + h.height, side: 'bottom' };
    default: return { x, y: y + h.height / 2, side: 'left' };
  }
}
// The geometry the routes depend on: every shown card's box, every line's two ends, and whether a card is being
// dragged, as one flat list of numbers plus the line ids. It is read on every store update (each pointer move of a
// drag, each pan), so it builds no strings; the Diagram re-renders only when a number or an id actually changes.
// Numbers are kept exactly, so a route starts on the very point the edge is drawn from.
type Geometry = { nums: number[]; ids: string[] };
const SIDES = ['top', 'right', 'bottom', 'left'];
const sideNo = (p: Position) => (p === Position.Top ? 0 : p === Position.Right ? 1 : p === Position.Bottom ? 2 : 3);
const NO_GEOMETRY: Geometry = { nums: [], ids: [] };
function geometryOf(s: ReactFlowState): Geometry {
  const nums: number[] = [0, 0], ids: string[] = [];
  let boxes = 0;
  for (const n of s.nodeLookup.values()) {
    if (n.hidden || !n.measured?.width || !n.measured?.height) continue;
    if (n.dragging) nums[0] = 1;
    nums.push(n.internals.positionAbsolute.x, n.internals.positionAbsolute.y, n.measured.width, n.measured.height);
    boxes++;
  }
  nums[1] = boxes;
  for (const e of s.edges) {
    if (e.hidden || e.source === e.target) continue;
    const a = s.nodeLookup.get(e.source), b = s.nodeLookup.get(e.target);
    if (!a || !b || a.hidden || b.hidden) continue;
    const hs = a.internals.handleBounds?.source?.find((h) => h.id === e.sourceHandle) as Bounds | undefined;
    // Loose connection mode, as React Flow resolves it: a target handle may be any handle of the target card.
    const tb = b.internals.handleBounds;
    const ht = (tb?.target?.find((h) => h.id === e.targetHandle) ?? tb?.source?.find((h) => h.id === e.targetHandle)) as Bounds | undefined;
    if (!hs || !ht) continue;
    const p = handleEnd(a, hs), q = handleEnd(b, ht);
    nums.push(p.x, p.y, sideNo(p.side as Position), q.x, q.y, sideNo(q.side as Position));
    ids.push(e.id);
  }
  return { nums, ids };
}
const sameGeometry = (a: Geometry, b: Geometry) => {
  if (a === b) return true;
  if (a.nums.length !== b.nums.length || a.ids.length !== b.ids.length) return false;
  for (let i = 0; i < a.nums.length; i++) if (a.nums[i] !== b.nums[i]) return false;
  for (let i = 0; i < a.ids.length; i++) if (a.ids[i] !== b.ids[i]) return false;
  return true;
};
const noGeometry = () => NO_GEOMETRY;
function unpack(g: Geometry): { dragging: boolean; boxes: RouteBox[]; lines: { id: string; src: RouteEnd; dst: RouteEnd }[] } {
  const n = g.nums, boxes: RouteBox[] = [], lines: { id: string; src: RouteEnd; dst: RouteEnd }[] = [];
  let k = 2;
  for (let i = 0; i < n[1]; i++, k += 4) boxes.push({ x: n[k], y: n[k + 1], w: n[k + 2], h: n[k + 3] });
  for (const id of g.ids) { lines.push({ id, src: { x: n[k], y: n[k + 1], side: SIDES[n[k + 2]] }, dst: { x: n[k + 3], y: n[k + 4], side: SIDES[n[k + 5]] } }); k += 6; }
  return { dragging: n[0] === 1, boxes, lines };
}
// The last settled pass, kept across mounts: going to Relationships and back redraws without routing again.
let lastPass: { geo: Geometry; routes: Map<string, RouteResult> } | null = null;
const NO_ROUTES = new Map<string, RouteResult>();   // the bus matrix before its first pass: no line drawn yet
const SETTLE_MS = 40;    // the quiet time that ends a burst of geometry changes
const MAX_WAIT_MS = 250; // a burst that never goes quiet still gets routed this often
const SLICE_MS = 12;     // how long the settled pass works before it lets the page answer again

/** Runs a generator a slice at a time, each slice a separate task, until it ends (done) or the returned stop is called. */
export function runInSlices<T>(job: Generator<unknown, T>, done: (value: T) => void): () => void {
  let stopped = false;
  const ch = new MessageChannel();   // a message, unlike a 0 ms timer, is not held back 4 ms once timers nest
  const step = () => {
    if (stopped) return;
    const end = performance.now() + SLICE_MS;
    for (;;) {
      const r = job.next();
      if (r.done) { stopped = true; ch.port1.close(); done(r.value); return; }
      if (performance.now() >= end) break;
    }
    ch.port2.postMessage(0);
  };
  ch.port1.onmessage = step;
  step();
  return () => { stopped = true; ch.port1.close(); };
}

/** Routes every bus matrix line in one pass per geometry change. Call inside the ReactFlowProvider. */
function useBusRoutes(on: boolean, hold: boolean): Map<string, RouteResult> | null {
  const geo = useStore(on ? geometryOf : noGeometry, sameGeometry);
  const prev = useRef<Map<string, RouteResult> | undefined>(undefined);
  const [settled, setSettled] = useState(0);   // bumped when a settled pass lands, to re-read it
  const routes = useMemo(() => {
    if (geo === NO_GEOMETRY) return null;
    if (lastPass && sameGeometry(lastPass.geo, geo)) return lastPass.routes;
    const { dragging, boxes, lines } = unpack(geo);
    // Mid-drag the routes must follow the card this frame: keep what the drag did not disturb, route the rest now.
    if (dragging) return routeAll(lines, boxes, prev.current ?? lastPass?.routes);
    return prev.current ? holdRoutes(prev.current, lines) : NO_ROUTES;   // held until the settled pass below replaces them
  }, [geo, settled]);
  prev.current = routes ?? prev.current;
  const burst = useRef(0);   // when the current burst of changes began
  useEffect(() => {
    if (geo === NO_GEOMETRY || (lastPass && sameGeometry(lastPass.geo, geo))) { burst.current = 0; return; }
    if (geo.nums[0] === 1 || hold) return;   // dragging: routed above, frame by frame; the drop settles it
    const now = performance.now();
    if (!burst.current) burst.current = now;
    let stop: (() => void) | null = null;
    const id = window.setTimeout(() => {
      burst.current = 0;
      const { boxes, lines } = unpack(geo);
      stop = runInSlices(routeSteps(lines, boxes), (routes) => { lastPass = { geo, routes }; setSettled((n) => n + 1); });
    }, Math.max(0, Math.min(SETTLE_MS, MAX_WAIT_MS - (now - burst.current))));
    return () => { window.clearTimeout(id); stop?.(); };
  }, [geo, hold]);
  return routes;
}

/** Hands the bus matrix routes to every line. Wrap the ReactFlow in it: only this and the lines re-render when the
 *  routes change, never the whole Diagram (its children are the same element each time, so React skips them). */
export function BusRoutes({ on, hold = false, children }: { on: boolean; hold?: boolean; children: ReactNode }) {
  return <RouteContext.Provider value={useBusRoutes(on, hold)}>{children}</RouteContext.Provider>;
}

export function SemRelEdge(props: EdgeProps) {
  const { id, source, target, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, style, data } = props;
  const motionKey = useRelationshipMotion(id);
  const d = data as { offset?: number; lane?: number; fromMany?: boolean; toMany?: boolean; title?: string; dim?: boolean; cell?: 'source' | 'target'; dx?: number; slots?: number; bracket?: { kind: 'left' | 'top'; near: boolean; from: number; to: number } } | undefined;
  // The matrix: a fact's link to a lookup table is a dot where the fact's row meets the lookup table's column.
  const factNode = useInternalNode(d?.cell === 'source' ? source : target);
  const dimNode = useInternalNode(d?.cell === 'source' ? target : source);
  // Bus matrix: the route the Diagram's one routing pass found for this line (useBusRoutes), or its last route held
  // on its new ends until that pass lands (drawn faded). A line with no route yet (the Diagram just opened) waits for
  // the first pass. No route at all (a card walled in on every side) falls back to the step line below.
  const routes = useContext(RouteContext);
  const srcNode = useInternalNode(source), dstNode = useInternalNode(target);
  if (d?.bracket) {
    // A link that is no dot (a header to its lines, two lookup tables that are not neighbours). The grid draws no long line for it:
    // a short mark between two cards that sit one under the other, else a small tag on each card that names its partner on hover.
    if (!srcNode?.measured?.width || !dstNode?.measured?.width) return null;
    const box = (n: NonNullable<typeof srcNode>) => ({ x: n.internals.positionAbsolute.x, y: n.internals.positionAbsolute.y, w: n.measured.width ?? 0, h: n.measured.height ?? 0 });
    const a = box(srcNode), b = box(dstNode);
    const color = ((style as { stroke?: string } | undefined)?.stroke) ?? 'var(--sem-accent)';
    const near = d.bracket.near ? nearMarkPoints(a, b) : null;
    const fade = d.dim ? { opacity: 0.12, transition: 'opacity 120ms' } : { transition: 'opacity 120ms' };
    if (near) {
      const srcUp = a.y <= b.y;   // the upper card's end leaves its bottom, the lower card's end its top
      return (
        <g style={fade}>
          <BaseEdge path={pathOf(near)} style={style} />
          {endMarker(near[0].x, near[0].y, srcUp ? Position.Bottom : Position.Top, srcUp ? d.fromMany : d.toMany, color, 's')}
          {endMarker(near[1].x, near[1].y, srcUp ? Position.Top : Position.Bottom, srcUp ? d.toMany : d.fromMany, color, 't')}
          {d.title ? <title>{d.title}</title> : null}
        </g>
      );
    }
    return <LinkTags from={srcNode.id} to={dstNode.id} a={a} b={b} kind={d.bracket.kind} places={[d.bracket.from, d.bracket.to]} style={style as { stroke?: string; strokeDasharray?: string } | undefined} style2={fade} title={d.title} />;
  }
  if (d?.cell) {
    const fw = factNode?.measured?.width, dw = dimNode?.measured?.width;
    if (!factNode || !dimNode || !fw || !dw) return null;
    return <MatrixDot id={id} slots={d.slots ?? 1} x={dimNode.internals.positionAbsolute.x + dw / 2 + (d.dx ?? 0)} y={factNode.internals.positionAbsolute.y + MATRIX_LANE}
      style={style as { stroke?: string; strokeDasharray?: string } | undefined} dim={!!d.dim} title={d.title} />;
  }
  const r = routes?.get(id);
  const fresh = !!r && Math.abs(r.src.x - sourceX) < 0.5 && Math.abs(r.src.y - sourceY) < 0.5 && Math.abs(r.dst.x - targetX) < 0.5 && Math.abs(r.dst.y - targetY) < 0.5;
  if (routes && source !== target && !fresh) return null;
  const routed = r?.pts ?? null;
  const offset = typeof d?.offset === 'number' ? d.offset : 10;
  const color = ((style as { stroke?: string } | undefined)?.stroke) ?? 'var(--sem-accent)';
  let path: string;
  if (source === target) {
    [path] = getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition, curvature: 0.7 });
  } else if (routed && routed.length > 1) {
    path = pathOf(routed);
  } else {
    const vert = (sourcePosition === Position.Top || sourcePosition === Position.Bottom)
              && (targetPosition === Position.Top || targetPosition === Position.Bottom);
    const horiz = (sourcePosition === Position.Left || sourcePosition === Position.Right)
               && (targetPosition === Position.Left || targetPosition === Position.Right);
    const lane = typeof d?.lane === 'number' ? d.lane : 0.5;
    // The single crossbar: its HEIGHT for a vertical edge (layered), its X for a horizontal edge (auto-arrange =
    // rotated layered). lane ∈ (0,1) interpolates between the two handles; offset:0 keeps it to exactly one crossbar.
    const centerY = vert ? targetY + lane * (sourceY - targetY) : undefined;
    const centerX = horiz ? targetX + lane * (sourceX - targetX) : undefined;
    // borderRadius:0 → hard right-angle corners (a true STEP line, Kane's pick) rather than rounded "curved" steps.
    [path] = getSmoothStepPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition, borderRadius: 0, offset: (vert || horiz) ? 0 : offset, centerX, centerY });
  }
  // React Flow ignores edge.title, so render a native SVG <title> inside the edge group for the hover tooltip.
  // When dimmed (another relationship is focused), wrap in a <g opacity> so the PATH AND ITS MARKERS fade together.
  // A held line (its old route, until the routing pass lands) fades with stroke-opacity, which the path and its markers inherit:
  // an opacity on the group made the software compositor take up to 1.9 s to draw one frame at 300 tables, with 278 lines held.
  return (
    <g style={d?.dim ? { opacity: 0.12, transition: 'opacity 120ms' } : r?.held ? { strokeOpacity: 0.4, fillOpacity: 0.4 } : { transition: 'opacity 120ms' }}>
      <RelationshipSnap eventKey={motionKey} enabled={motionKey > 0} labels={<>
        {endMarker(sourceX, sourceY, sourcePosition, d?.fromMany, color, 's')}
        {endMarker(targetX, targetY, targetPosition, d?.toMany, color, 't')}
        {motionKey > 0 && <><text x={sourceX + 12} y={sourceY - 8} fill={color} fontSize="var(--sem-text-meta)">{d?.fromMany ? '*' : '1'}</text>
          <text x={targetX - 16} y={targetY - 8} fill={color} fontSize="var(--sem-text-meta)">{d?.toMany ? '*' : '1'}</text></>}
      </>}><BaseEdge path={path} style={style} /></RelationshipSnap>
      {d?.title ? <title>{d.title}</title> : null}
    </g>
  );
}

// A link the grid does not draw as a line: a small tag on each of its two cards (a pair of rings, the sign of a link). Resting on the
// edge of the card, so it never covers a dot. Pointing at one names the link and outlines the partner card, so the pair is plain.
function LinkTags({ from, to, a, b, kind, places, style, style2, title }: { from: string; to: string; a: Box; b: Box; kind: 'left' | 'top'; places: [number, number]; style?: { stroke?: string; strokeDasharray?: string }; style2: CSSProperties; title?: string }) {
  const [on, setOn] = useState(false);
  const color = style?.stroke ?? 'var(--sem-accent)';
  const off = !!style?.strokeDasharray;
  const partner = (id: string, add: boolean) => { const el = document.querySelector(`.react-flow__node[data-id="${CSS.escape(id)}"]`); if (el) el.classList.toggle('sem-link-partner', add); };
  const hover = (add: boolean, other: string) => { setOn(add); partner(other, add); };
  const tag = (box: Box, place: number, other: string, key: string) => {
    const p = tagPoint(kind, box, place);
    return (
      <g key={key} data-link-tag={key} transform={`translate(${p.x},${p.y})`} style={{ cursor: 'pointer' }} onMouseEnter={() => hover(true, other)} onMouseLeave={() => hover(false, other)}>
        <rect x={-9} y={-9} width={18} height={18} fill="transparent" stroke="none" style={{ pointerEvents: 'all' }} />
        <rect x={-7} y={-5} width={14} height={10} rx={5} style={{ fill: on ? color : 'var(--sem-surface)', stroke: color, strokeWidth: 1.4, strokeDasharray: off ? '2 2' : undefined }} />
        <circle cx={-2} cy={0} r={2.4} style={{ fill: 'none', stroke: on ? 'var(--sem-surface)' : color, strokeWidth: 1.2 }} />
        <circle cx={2} cy={0} r={2.4} style={{ fill: 'none', stroke: on ? 'var(--sem-surface)' : color, strokeWidth: 1.2 }} />
        {title ? <title>{title}</title> : null}
      </g>
    );
  };
  return <g style={style2}>{tag(a, places[0], to, 's')}{tag(b, places[1], from, 't')}</g>;
}
type Box = { x: number; y: number; w: number; h: number };

/** Side by side, two links of one cell sit this far apart: the discs (r 6 and the both-ways ring r 9) must not touch. */
export const DOT_SLOT = 20;

// One cell of the matrix. A switched-off link is a ring, a link that filters both ways is a dot with a ring round it, and a
// plain one-way link is a dot. The disc behind it is the click target, so a small dot is easy to hit.
function MatrixDot({ id, x, y, style, dim, title, slots }: { id: string; slots: number; x: number; y: number; style?: { stroke?: string; strokeDasharray?: string }; dim: boolean; title?: string }) {
  const color = style?.stroke ?? 'var(--sem-accent)';
  const off = !!style?.strokeDasharray;
  const both = color === 'var(--sem-warn)';
  return (
    // React Flow gives an edge pointer-events: visibleStroke, which a filled dot with no stroke would miss: the disc says "all".
    <g data-matrix-dot={id} style={{ opacity: dim ? 0.12 : 1, transition: 'opacity 120ms', cursor: 'pointer' }}>
      <circle cx={x} cy={y} r={slots > 1 ? DOT_SLOT / 2 : 13} fill="transparent" stroke="none" style={{ pointerEvents: 'all' }} />
      {both && <circle cx={x} cy={y} r={9} fill="none" style={{ stroke: color, strokeWidth: 1.4 }} />}
      <circle cx={x} cy={y} r={off ? 5.5 : 6} style={off ? { fill: 'var(--sem-surface)', stroke: color, strokeWidth: 1.6 } : { fill: color, stroke: 'var(--sem-surface)', strokeWidth: 1.25 }} />
      {title ? <title>{title}</title> : null}
    </g>
  );
}

// The matrix's rows and columns, one quiet line each, drawn once for the whole diagram (a line per link would stack up
// near the cards). A row runs from its fact's right edge to the last dot in it, a column from its lookup table's bottom
// edge to the last dot in it.
type Guide = { id: string; x1: number; y1: number; x2: number; y2: number };
function guidesOf(s: ReactFlowState): number[] {
  const rows = new Map<string, number[]>(), cols = new Map<string, number[]>();
  for (const e of s.edges) {
    const side = (e.data as { cell?: 'source' | 'target' } | undefined)?.cell;
    if (!side || e.hidden) continue;
    const fact = s.nodeLookup.get(side === 'source' ? e.source : e.target), dim = s.nodeLookup.get(side === 'source' ? e.target : e.source);
    if (!fact || !dim || !fact.measured?.width || !dim.measured?.width) continue;
    const fx = fact.internals.positionAbsolute.x, fy = fact.internals.positionAbsolute.y;
    const dx = dim.internals.positionAbsolute.x, dy = dim.internals.positionAbsolute.y;
    const colX = dx + dim.measured.width / 2, dotX = colX + ((e.data as { dx?: number }).dx ?? 0), dotY = fy + MATRIX_LANE;   // a column stays on the cell's middle; side by side dots sit round it
    const row = rows.get(fact.id), col = cols.get(dim.id);
    if (!row) rows.set(fact.id, [fx + fact.measured.width, dotY, dotX]); else row[2] = Math.max(row[2], dotX);
    if (!col) cols.set(dim.id, [colX, dy + (dim.measured.height ?? 0), dotY]); else col[2] = Math.max(col[2], dotY);
  }
  const out: number[] = [];
  for (const r of rows.values()) out.push(0, r[0], r[1], r[2], r[1]);
  for (const c of cols.values()) out.push(1, c[0], c[1], c[0], c[2]);
  return out;
}
const sameNumbers = (a: number[], b: number[]) => a === b || (a.length === b.length && a.every((v, i) => v === b[i]));
export function MatrixGuides() {
  const g = useStore(guidesOf, sameNumbers);
  const lines: Guide[] = [];
  for (let i = 0; i < g.length; i += 5) lines.push({ id: `${g[i]}:${i}`, x1: g[i + 1], y1: g[i + 2], x2: g[i + 3], y2: g[i + 4] });
  if (!lines.length) return null;
  return (
    <ViewportPortal>
      <svg width={1} height={1} style={{ position: 'absolute', overflow: 'visible', pointerEvents: 'none', zIndex: -1 }} aria-hidden data-matrix-guides="true">
        {lines.map((l) => <line key={l.id} x1={l.x1} y1={l.y1} x2={l.x2} y2={l.y2} style={{ stroke: 'var(--sem-border)', strokeWidth: 1 }} />)}
      </svg>
    </ViewportPortal>
  );
}

export const edgeTypes = { semrel: SemRelEdge };

// The INDICATIVE line shown while dragging column→column to create a relationship (React Flow's connectionLineComponent).
// Deliberately distinct from the permanent edges (which keep their accent crow's-foot look): a bright-green stepped
// line with a polished "ball" at each end — the drag origin and the cursor — each ringed + haloed so it reads clearly
// over tables. Purely a drag affordance; it vanishes on drop (the real edge is then drawn by SemRelEdge).
export function RelConnectionLine({ fromX, fromY, toX, toY, fromPosition, toPosition }: ConnectionLineComponentProps) {
  const [path] = getSmoothStepPath({
    sourceX: fromX, sourceY: fromY, sourcePosition: fromPosition,
    targetX: toX, targetY: toY, targetPosition: toPosition, borderRadius: 0,
  });
  const c = 'var(--sem-rel)';
  return (
    <g>
      <path d={path} fill="none" strokeLinecap="round" style={{ stroke: c, strokeWidth: 2 }} />
      {[[fromX, fromY], [toX, toY]].map(([x, y], i) => (
        <g key={i}>
          <circle cx={x} cy={y} r={6} style={{ fill: c, opacity: 0.22 }} />
          <circle cx={x} cy={y} r={3.5} style={{ fill: c, stroke: 'var(--sem-surface)', strokeWidth: 1.25 }} />
        </g>
      ))}
    </g>
  );
}
