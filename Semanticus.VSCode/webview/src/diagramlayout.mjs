// The Diagram's layout rules, in one plain ES module, so the canvas (diagram.tsx) and the exported document
// (docrender.ts) place and anchor their cards with the SAME code and can never drift apart. Pure: no React, no DOM.
// diagramroute.mjs owns the placement grid (busGrid) and the line router (routeAll); this file owns what feeds them:
// the table roles, the card boxes, the anchor sides, and the bus matrix order.
// Sides are the strings React Flow's Position enum holds ('top' | 'right' | 'bottom' | 'left'), so the canvas passes
// them straight to its handles.
import { busGrid, busOrder, routeAll } from './diagramroute.mjs';

/** @typedef {'top' | 'right' | 'bottom' | 'left'} Side */
/** @typedef {{ x: number, y: number }} XY */
/** @typedef {{ w: number, h: number }} Size */
/** @typedef {{ rel: string, kind: 'source' | 'target', side: Side, frac: number }} Anchor */
/** @typedef {'free' | 'hierarchy' | 'busmatrix' | 'layered'} LayoutMode */

export const NODE_W = 190;   // minimum node width (collapsed)
export const NODE_H = 92;    // minimum node height (collapsed)
// Dynamic node sizing: a node grows on the axis its relationship anchors run along, so many incident
// relationships spread into their own orthogonal lanes instead of bundling on a short fixed edge (the
// "collapsed lines" near a busy node). With frac=(i+1)/(n+1), n anchors on a side need (n+1)*SLOT of length
// to stay SLOT apart — so WIDTH grows with the Top/Bottom anchor count, HEIGHT with the Left/Right count.
export const SLOT = 30;

// The box a node needs so its anchors stay SLOT apart: width from the busier of its Top/Bottom edges, height
// from the busier of its Left/Right edges. A node with few/no relationships stays at the NODE_W×NODE_H minimum.
// Anchor sides depend on the layout (see computeAnchors), so size is always derived from THAT layout's anchors —
// keeping the rendered node, its handle spread, and the layout spacing in perfect agreement.
/** @param {Anchor[]} anchors @returns {Size} */
export function sizeForAnchors(anchors) {
  let top = 0, bottom = 0, left = 0, right = 0;
  for (const a of anchors) {
    if (a.side === 'top') top++;
    else if (a.side === 'bottom') bottom++;
    else if (a.side === 'left') left++;
    else right++;
  }
  const horiz = Math.max(top, bottom);   // anchors along a horizontal edge → need WIDTH to separate
  const vert = Math.max(left, right);    // anchors along a vertical edge → need HEIGHT to separate
  return { w: Math.max(NODE_W, (horiz + 1) * SLOT), h: Math.max(NODE_H, (vert + 1) * SLOT) };
}

// Classify each table by its Kimball role from the relationships: a table referenced as a "one" end is a
// DIMENSION, a pure "many" end is a FACT, and one with no relationships is ISOLATED. Also returns each table's
// neighbour set (used to order ranks so related tables line up). Shared by the bus-matrix + layered layouts and
// computeAnchors so handle sides always agree with the placement.
/** @param {{ name: string }[]} tables @param {{ fromTable: string, toTable: string, fromCardinality: string, toCardinality: string }[]} rels */
export function classifyRoles(tables, rels) {
  const names = new Set(tables.map((t) => t.name));
  /** @type {Set<string>} */ const isOne = new Set();
  /** @type {Set<string>} */ const isMany = new Set();
  /** @type {Map<string, Set<string>>} */ const nbr = new Map();
  const link = (/** @type {string} */ a, /** @type {string} */ b) => { let s = nbr.get(a); if (!s) nbr.set(a, s = new Set()); s.add(b); };
  for (const r of rels) {
    if (!names.has(r.fromTable) || !names.has(r.toTable)) continue;
    const { manyT, oneT } = roleOf(r);
    isMany.add(manyT); isOne.add(oneT);
    link(r.fromTable, r.toTable); link(r.toTable, r.fromTable);
  }
  /** @type {string[]} */ const dims = [];
  /** @type {string[]} */ const facts = [];
  /** @type {string[]} */ const iso = [];
  for (const t of tables) {
    if (isOne.has(t.name)) dims.push(t.name);          // dimension — referenced by something (incl. snowflake outriggers)
    else if (isMany.has(t.name)) facts.push(t.name);   // pure fact — only ever the many side
    else iso.push(t.name);                             // isolated — no relationships
  }
  return { dims, facts, iso, neighbors: (/** @type {string} */ n) => nbr.get(n) ?? new Set() };
}

// Order one rank so each node sits under/over the mean position of its neighbours in the other rank (the classic
// barycenter heuristic) — the single biggest reducer of edge crossings, so relationship lines fan out cleanly
// instead of tangling. Nodes with no placed neighbour fall to the end, in their original order.
/** @param {string[]} rank @param {Map<string, number>} otherIndex @param {(n: string) => Set<string>} neighbors @returns {string[]} */
export function baryOrder(rank, otherIndex, neighbors) {
  const key = (/** @type {string} */ n) => {
    const ix = [...neighbors(n)].map((m) => otherIndex.get(m)).filter((v) => v != null);
    return ix.length ? ix.reduce((a, b) => a + b, 0) / ix.length : Number.MAX_SAFE_INTEGER;
  };
  return rank.map((n, i) => ({ n, i, k: key(n) })).sort((a, b) => (a.k - b.k) || (a.i - b.i)).map((x) => x.n);
}

// Bus matrix (Kimball): DIMENSIONS in ONE row across the TOP (connecting from their BOTTOM edge), FACTS in a column
// down the LEFT under that row (joining on their RIGHT edge), unlinked tables on the right. Each node is laid by its
// DYNAMIC box, so a fact with many relationships is TALLER and a dim used by many facts is WIDER (their anchors spread
// into distinct lanes). Kane, 8 Oct: the row never wraps (the 27 Sep space rule did, and put lines behind cards and
// lookup tables among the facts); a wide model scrolls sideways and opens on its facts at a readable zoom. busOrder
// orders the row and the column for the fewest crossings and puts each outrigger right after the table it hangs off.
// `aspect` is no longer used; busGrid keeps it in its signature. Gaps: 72 between facts, 96 between dimensions, 110
// under the row.
/** @param {{ name: string }[]} tables @param {any[]} rels @param {(name: string) => Size} sizeOf @param {number} [aspect] @returns {Map<string, XY>} */
export function busMatrixPositions(tables, rels, sizeOf, aspect) {
  const links = rels.map((r) => { const { manyT, oneT } = roleOf(r); return { many: manyT, one: oneT }; });
  const { dims: dimOrder, facts: factOrder, iso } = busOrder(tables.map((t) => t.name), links);
  const g = busGrid(dimOrder.map(sizeOf), factOrder.map(sizeOf), iso.map(sizeOf), aspect, { nodeW: NODE_W, nodeH: NODE_H });
  /** @type {Map<string, XY>} */ const m = new Map();
  dimOrder.forEach((n, i) => m.set(n, g.dims[i]));    // dims in one row across the top
  factOrder.forEach((n, i) => m.set(n, g.facts[i]));  // facts down the left
  iso.forEach((n, i) => m.set(n, g.iso[i]));          // unlinked on the far right
  return m;
}

// Many/One role of a relationship's two ends — mirrors busMatrixPositions's classification so handle sides agree.
/** @param {{ fromTable: string, toTable: string, fromCardinality: string, toCardinality: string }} r */
export function roleOf(r) {
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
/** @param {Side} side @param {Map<string, XY>} centers @param {string} table @param {string} other */
export function anchorKey(side, centers, table, other) {
  const a = centers.get(table), b = centers.get(other);
  if (!a || !b) return 0;
  const dx = b.x - a.x, dy = b.y - a.y;
  if (side === 'top') return Math.atan2(dx, -dy);     // left → right along the top edge
  if (side === 'bottom') return Math.atan2(dx, dy);   // left → right along the bottom edge
  if (side === 'right') return Math.atan2(dy, dx);    // top → bottom down the right edge
  return Math.atan2(dy, -dx);                         // top → bottom down the left edge
}

/** @param {{ name: string }[]} members @param {{ name: string, fromTable: string, toTable: string, fromCardinality: string, toCardinality: string }[]} rels @param {LayoutMode} layout @param {Map<string, XY>} [centers] @returns {Map<string, Anchor[]>} */
export function computeAnchors(members, rels, layout, centers) {
  const names = new Set(members.map((t) => t.name));
  /** @type {{ table: string, other: string, rel: string, kind: 'source' | 'target', side: Side }[]} */
  const ends = [];
  // Dimensions: the one side of some shown link (the bus matrix lays them in its row).
  const oneSides = new Set(rels.filter((r) => names.has(r.fromTable) && names.has(r.toTable)).map((r) => roleOf(r).oneT));
  for (const r of rels) {
    if (!names.has(r.fromTable) || !names.has(r.toTable)) continue;
    /** @type {Side} */ let fromSide;
    /** @type {Side} */ let toSide;
    if (layout === 'layered') {
      // Vertical ranks (facts below, dims above): the one-side (dimension) faces DOWN toward the facts, the many-side
      // (fact) faces UP toward the dims — clean vertical lanes between adjacent ranks.
      const { oneT } = roleOf(r);
      fromSide = r.fromTable === oneT ? 'bottom' : 'top';
      toSide = r.toTable === oneT ? 'bottom' : 'top';
    } else if (layout === 'busmatrix') {
      // Bus matrix (dims top, facts left): the fact (many-side) joins on its RIGHT, the dimension (one-side) connects
      // from its BOTTOM — an orthogonal L-route between the left fact column and the top dim row. A link between two
      // dimensions (an outrigger, which sits in the row beside the table it hangs off) runs side to side along the row:
      // the facing sides, left one's RIGHT to right one's LEFT (Kane, 8 Oct). With no positions yet, many RIGHT.
      const { manyT, oneT } = roleOf(r);
      if (oneSides.has(manyT)) {
        const m = centers?.get(manyT), o = centers?.get(oneT);
        const manyLeft = !m || !o || m.x <= o.x;
        fromSide = (r.fromTable === manyT) === manyLeft ? 'right' : 'left';
        toSide = (r.toTable === manyT) === manyLeft ? 'right' : 'left';
      } else {
        fromSide = r.fromTable === oneT ? 'bottom' : 'right';
        toSide = r.toTable === oneT ? 'bottom' : 'right';
      }
    } else if (layout === 'hierarchy') {
      // Vertical (layered rotated 90°: facts left, dims right): the many-side (fact) leaves its RIGHT, the
      // one-side (dimension) connects on its LEFT — horizontal lanes with a single vertical crossbar (mirrors layered).
      const { oneT } = roleOf(r);
      fromSide = r.fromTable === oneT ? 'left' : 'right';
      toSide = r.toTable === oneT ? 'left' : 'right';
    } else {
      fromSide = 'right'; toSide = 'left';
    }
    ends.push({ table: r.fromTable, other: r.toTable, rel: r.name, kind: 'source', side: fromSide });
    ends.push({ table: r.toTable, other: r.fromTable, rel: r.name, kind: 'target', side: toSide });
  }
  /** @type {Map<string, typeof ends>} */ const groups = new Map();
  for (const e of ends) { const k = e.table + '|' + e.side; const g = groups.get(k); if (g) g.push(e); else groups.set(k, [e]); }
  /** @type {Map<string, Anchor[]>} */ const out = new Map();
  for (const g of groups.values()) {
    // Sort the ends sharing this edge by the angle to their connected table (when positions are known), so the
    // attach points run sequentially along the edge instead of in arbitrary relationship order.
    if (centers) g.sort((a, b) => anchorKey(a.side, centers, a.table, a.other) - anchorKey(b.side, centers, b.table, b.other));
    g.forEach((e, i) => {
      /** @type {Anchor} */ const a = { rel: e.rel, kind: e.kind, side: e.side, frac: (i + 1) / (g.length + 1) };
      const arr = out.get(e.table); if (arr) arr.push(a); else out.set(e.table, [a]);
    });
  }
  return out;
}

// How far a line end sits off its card side: React Flow centres an 8 px handle on the side, and a line starts at the
// handle's outer edge (handleEnd, diagramedges.tsx), so the static document starts its lines on the same point.
const HANDLE = 4;

/**
 * The Diagram of the bus matrix as plain geometry, for a reader with no canvas (the exported document). It runs the
 * same steps the canvas runs (diagram.tsx): anchors, box sizes, busMatrixPositions, anchors again from the card
 * centres, then routeAll through the channels between cards. Every table that takes part in a relationship is
 * placed, hidden ones included (a hidden fact table is usual practice and every line leaves it). Tables with no
 * relationship are returned in `isolated` and are not placed: the document names them in one line instead.
 * @template {{ name: string }} T
 * @template {{ name: string, fromTable: string, toTable: string, fromCardinality: string, toCardinality: string }} R
 * @param {{ tables: T[], relationships: R[] }} graph
 * @param {{ aspect?: number }} [opts]
 */
export function staticDiagram(graph, { aspect = 1.6 } = {}) {
  const tables = graph.tables ?? [];
  const rels = graph.relationships ?? [];
  const { iso } = classifyRoles(tables, rels);
  const isoSet = new Set(iso);
  const linked = tables.filter((t) => !isoSet.has(t.name));
  const isolated = tables.filter((t) => isoSet.has(t.name));
  if (linked.length === 0) return { cards: [], lines: [], isolated };
  const sides0 = computeAnchors(linked, rels, 'busmatrix');
  const sizeOf = (/** @type {string} */ n) => sizeForAnchors(sides0.get(n) ?? []);
  const pos = busMatrixPositions(linked, rels, sizeOf, aspect);
  const centers = new Map(linked.map((t) => { const p = /** @type {XY} */ (pos.get(t.name)), s = sizeOf(t.name); return [t.name, { x: p.x + s.w / 2, y: p.y + s.h / 2 }]; }));
  const anchors = computeAnchors(linked, rels, 'busmatrix', centers);
  const cards = linked.map((t) => ({ t, ...(/** @type {XY} */ (pos.get(t.name))), ...sizeForAnchors(anchors.get(t.name) ?? []) }));
  const box = new Map(cards.map((c) => [c.t.name, c]));
  const endOf = (/** @type {string} */ table, /** @type {string} */ rel, /** @type {'source'|'target'} */ kind) => {
    const a = (anchors.get(table) ?? []).find((x) => x.rel === rel && x.kind === kind);
    const c = /** @type {NonNullable<ReturnType<typeof box.get>>} */ (box.get(table));
    if (!a) return { x: c.x + c.w / 2, y: c.y + c.h / 2, side: 'right' };
    if (a.side === 'right') return { x: c.x + c.w + HANDLE, y: c.y + a.frac * c.h, side: 'right' };
    if (a.side === 'bottom') return { x: c.x + a.frac * c.w, y: c.y + c.h + HANDLE, side: 'bottom' };
    if (a.side === 'left') return { x: c.x - HANDLE, y: c.y + a.frac * c.h, side: 'left' };
    return { x: c.x + a.frac * c.w, y: c.y - HANDLE, side: 'top' };
  };
  const drawn = rels.filter((r) => box.has(r.fromTable) && box.has(r.toTable));
  const ends = drawn.map((r) => ({ id: r.name, src: endOf(r.fromTable, r.name, 'source'), dst: endOf(r.toTable, r.name, 'target') }));
  const routes = routeAll(ends, cards.map((c) => ({ x: c.x, y: c.y, w: c.w, h: c.h })));
  const lines = drawn.map((r, i) => ({ r, ...ends[i], pts: routes.get(r.name)?.pts ?? null }));
  return { cards, lines, isolated };
}
