import { BaseEdge, getSmoothStepPath, getBezierPath, Position, type EdgeProps, type ConnectionLineComponentProps } from '@xyflow/react';

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

export function SemRelEdge(props: EdgeProps) {
  const { source, target, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, style, data } = props;
  const d = data as { offset?: number; lane?: number; fromMany?: boolean; toMany?: boolean; title?: string; dim?: boolean } | undefined;
  const offset = typeof d?.offset === 'number' ? d.offset : 10;
  const color = ((style as { stroke?: string } | undefined)?.stroke) ?? 'var(--sem-accent)';
  let path: string;
  if (source === target) {
    [path] = getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition, curvature: 0.7 });
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
  return (
    <g style={d?.dim ? { opacity: 0.12, transition: 'opacity 120ms' } : { transition: 'opacity 120ms' }}>
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
