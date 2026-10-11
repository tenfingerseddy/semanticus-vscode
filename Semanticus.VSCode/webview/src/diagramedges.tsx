import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { BaseEdge, getSmoothStepPath, getBezierPath, Position, useStore, type EdgeProps, type ConnectionLineComponentProps, type ReactFlowState } from '@xyflow/react';
import { routeAll, routeSteps, holdRoutes, pathOf, type RouteBox, type RouteEnd, type RouteResult } from './diagramroute.mjs';

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
  const d = data as { offset?: number; lane?: number; fromMany?: boolean; toMany?: boolean; title?: string; dim?: boolean } | undefined;
  // Bus matrix: the route the Diagram's one routing pass found for this line (useBusRoutes), or its last route held
  // on its new ends until that pass lands (drawn faded). A line with no route yet (the Diagram just opened) waits for
  // the first pass. No route at all (a card walled in on every side) falls back to the step line below.
  const routes = useContext(RouteContext);
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
      <BaseEdge path={path} style={style} />
      {endMarker(sourceX, sourceY, sourcePosition, d?.fromMany, color, 's')}
      {endMarker(targetX, targetY, targetPosition, d?.toMany, color, 't')}
      {d?.title ? <title>{d.title}</title> : null}
    </g>
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
