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
/** @typedef {'free' | 'hierarchy' | 'busmatrix' | 'layered' | 'matrix'} LayoutMode */

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
  // Ends that share one point on a side (the matrix puts every link of a fact on ONE lane) need no extra room.
  const top = new Set(), bottom = new Set(), left = new Set(), right = new Set();
  for (const a of anchors) {
    if (a.side === 'top') top.add(a.frac);
    else if (a.side === 'bottom') bottom.add(a.frac);
    else if (a.side === 'left') left.add(a.frac);
    else right.add(a.frac);
  }
  const horiz = Math.max(top.size, bottom.size);   // anchors along a horizontal edge → need WIDTH to separate
  const vert = Math.max(left.size, right.size);    // anchors along a vertical edge → need HEIGHT to separate
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

// ---- The matrix (Kane, 12 Oct: "the hero shot needs to be a bus matrix layout") --------------------------------------
// A true bus matrix, as Kimball draws it: lookup tables in ONE row across the top, facts in ONE column down the left,
// and a dot where a fact uses a lookup table. Every link of a fact runs along its row and every link of a lookup table
// down its column, so lines never cross each other and nothing is routed. The row starts with the date table, then the
// lookup tables the most facts share, so the shared ones read first. A lookup table only other lookup tables use sits
// beside the one it hangs off. Unlinked tables go in rows under the facts, never in the row of lookup tables.
export const MATRIX_FACT_W = NODE_W;
export const MATRIX_GAP = 20;            // between two lookup tables
export const MATRIX_CHILD_GAP = 36;      // between a lookup table and an outrigger beside it (a line and its ends fit)
export const MATRIX_LEFT = 48;           // from the fact column to the first lookup table (room for the row lines)
export const MATRIX_TOP = 44;            // from the row of lookup tables to the first fact (room for the column lines)
export const MATRIX_ROW_GAP = 20;        // between two facts
export const MATRIX_FACT_H = 72;         // a fact's card in the matrix: a name and one line, so its rows sit close
export const MATRIX_LANE = MATRIX_FACT_H / 2;   // a fact's row runs this far below its top edge, open or not
/** A lookup table's width: the more of them, the narrower, so a whole model fits one screen at a readable size. */
export function matrixDimWidth(count) { return count <= 6 ? NODE_W : count <= 9 ? 164 : count <= 12 ? 140 : 108; }
/** The gap between two lookup tables: tighter once there are many. */
export function matrixGap(count) { return count <= 9 ? MATRIX_GAP : 10; }

/**
 * A header table: the "one" end of a link from a fact (an Order Header over Order Line) that also has links of its own
 * to lookup tables a fact uses (Date, Customer). It belongs with the facts, so those links are dots in its own row.
 * @param {{ name: string }[]} tables @param {any[]} rels @returns {Set<string>}
 */
export function matrixHeaders(tables, rels) {
  const names = new Set(tables.map((t) => t.name));
  const links = rels.filter((r) => names.has(r.fromTable) && names.has(r.toTable) && r.fromTable !== r.toTable).map(roleOf);
  const ones = new Set(links.map((l) => l.oneT)), manys = new Set(links.map((l) => l.manyT));
  const pure = new Set([...manys].filter((n) => !ones.has(n)));
  const primary = new Set(links.filter((l) => pure.has(l.manyT)).map((l) => l.oneT));
  return new Set([...ones].filter((h) => links.some((l) => l.oneT === h && pure.has(l.manyT))
    && links.filter((l) => l.manyT === h && l.oneT !== h && primary.has(l.oneT)).length >= 2));
}

/** The links that are dots: a fact (never a lookup table) to a lookup table. @param {{ name: string }[]} tables @param {any[]} rels */
export function cellRels(tables, rels) {
  const names = new Set(tables.map((t) => t.name));
  const shown = rels.filter((r) => names.has(r.fromTable) && names.has(r.toTable));
  const headers = matrixHeaders(tables, rels);
  const ones = new Set(shown.map((r) => roleOf(r).oneT).filter((n) => !headers.has(n)));
  return new Set(shown.filter((r) => { const { manyT, oneT } = roleOf(r); return !headers.has(oneT) && !ones.has(manyT); }).map((r) => r.name));
}

/**
 * Where each dot sits in its cell. Two or more links between the same fact and lookup table (OrderDate on, ShipDate off)
 * sit side by side, the active ones first, so none hides another. @param {{ name: string }[]} tables @param {any[]} rels
 * @returns {Map<string, { i: number, n: number }>}
 */
export function cellSlots(tables, rels) {
  const cells = cellRels(tables, rels);
  /** @type {Map<string, any[]>} */ const by = new Map();
  for (const r of rels) if (cells.has(r.name)) { const { manyT, oneT } = roleOf(r); const k = manyT + '|' + oneT; const g = by.get(k); if (g) g.push(r); else by.set(k, [r]); }
  /** @type {Map<string, { i: number, n: number }>} */ const out = new Map();
  for (const g of by.values()) {
    [...g].sort((a, b) => (b.isActive === false ? 0 : 1) - (a.isActive === false ? 0 : 1)).forEach((r, i) => out.set(r.name, { i, n: g.length }));
  }
  return out;
}

/**
 * The links that are not dots and do not run straight between two neighbours: a header to its lines (fact to fact), and two
 * lookup tables that are not next to each other in the row. The grid draws no long line for these. Two facts one under the
 * other get a short mark between them (`near`); every other one is a small tag on each card that names the partner on hover.
 * `from` and `to` are each end's place among the tags on its card, so two tags never sit on one spot.
 * @param {{ name: string, isDateTable?: boolean }[]} tables @param {any[]} rels
 * @returns {Map<string, { kind: 'left' | 'top', near: boolean, from: number, to: number }>}
 */
export function matrixMarks(tables, rels) {
  const names = new Set(tables.map((t) => t.name));
  const cells = cellRels(tables, rels);
  const { dims, facts } = matrixOrder(tables, rels);
  const ri = new Map(dims.map((d, i) => [d, i])), fi = new Map(facts.map((f, i) => [f, i]));
  /** @type {{ r: any, kind: 'left' | 'top', near: boolean }[]} */ const found = [];
  for (const r of rels) {
    if (!names.has(r.fromTable) || !names.has(r.toTable) || cells.has(r.name) || r.fromTable === r.toTable) continue;
    const a = r.fromTable, b = r.toTable;
    if (fi.has(a) && fi.has(b)) found.push({ r, kind: 'left', near: Math.abs(fi.get(a) - fi.get(b)) === 1 });
    else if (ri.has(a) && ri.has(b) && Math.abs(ri.get(a) - ri.get(b)) !== 1) found.push({ r, kind: 'top', near: false });
  }
  /** @type {Map<string, number>} */ const used = new Map();
  const place = (/** @type {string} */ card, /** @type {string} */ kind) => { const k = card + '|' + kind; const n = used.get(k) ?? 0; used.set(k, n + 1); return n; };
  /** @type {Map<string, { kind: 'left' | 'top', near: boolean, from: number, to: number }>} */ const out = new Map();
  for (const f of [...found].sort((x, y) => (x.r.name < y.r.name ? -1 : 1))) {
    out.set(f.r.name, { kind: f.kind, near: f.near, from: place(f.r.fromTable, f.kind), to: place(f.r.toTable, f.kind) });
  }
  return out;
}

/**
 * The short mark between two cards one under the other (boxes { x, y, w, h }): a straight run through the gap, on the left
 * of the cards. Null when the cards have been moved apart, so the grid never draws a long line (the tags take over).
 * @param {{ x: number, y: number, w: number, h: number }} a @param {{ x: number, y: number, w: number, h: number }} b
 */
export function nearMarkPoints(a, b) {
  const [up, down] = a.y <= b.y ? [a, b] : [b, a];
  const gap = down.y - (up.y + up.h), x = Math.max(up.x, down.x) + 24;
  if (gap < 0 || gap > 48 || x > Math.min(up.x + up.w, down.x + down.w) - 8) return null;
  return a === up ? [{ x, y: up.y + up.h }, { x, y: down.y }] : [{ x, y: down.y }, { x, y: up.y + up.h }];
}

/** Where a tag sits: just outside its card's edge (never under the card), fact cards on the left, lookup cards on top, `place` apart each. @param {'left' | 'top'} kind @param {{ x: number, y: number, w: number, h: number }} box @param {number} place */
export function tagPoint(kind, box, place) {
  return kind === 'left' ? { x: box.x - 12, y: box.y + 14 + place * 18 } : { x: box.x + 14 + place * 18, y: box.y - 12 };
}

/** Who is who in the matrix, in reading order. @param {{ name: string, isDateTable?: boolean }[]} tables @param {any[]} rels */
export function matrixOrder(tables, rels) {
  const headers = matrixHeaders(tables, rels);
  // A link INTO a header is fact to fact: it takes no part in who is a lookup table.
  const links = rels.map((r) => { const { manyT, oneT } = roleOf(r); return { many: manyT, one: oneT }; }).filter((l) => !headers.has(l.one));
  const { dims, facts, iso, parent } = busOrder(tables.map((t) => t.name), links);
  const byName = new Map(tables.map((t) => [t.name, t]));
  const pos = new Map(tables.map((t, i) => [t.name, i]));
  const factSet = new Set(facts);
  const used = new Map();   // lookup table -> how many facts use it
  const count = new Map();  // fact -> how many lookup tables it uses
  for (const l of links) if (factSet.has(l.many)) {
    used.set(l.one, (used.get(l.one) ?? 0) + 1); count.set(l.many, (count.get(l.many) ?? 0) + 1);
  }
  const date = (/** @type {string} */ n) => (byName.get(n)?.isDateTable ? 0 : 1);
  const top = dims.filter((d) => parent[d] === undefined)
    .sort((a, b) => date(a) - date(b) || (used.get(b) ?? 0) - (used.get(a) ?? 0) || pos.get(a) - pos.get(b));
  const kids = new Map();
  for (const d of dims) if (parent[d] !== undefined) { const k = kids.get(parent[d]); if (k) k.push(d); else kids.set(parent[d], [d]); }
  /** @type {string[]} */ const row = [];
  const emit = (/** @type {string} */ d) => { row.push(d); for (const k of kids.get(d) ?? []) emit(k); };
  top.forEach(emit);
  const orderedFacts = [...facts].sort((a, b) => (count.get(b) ?? 0) - (count.get(a) ?? 0) || pos.get(a) - pos.get(b));
  for (const h of headers) {   // a header sits right above its first lines, so the link between them is short
    const lines = rels.map(roleOf).filter((l) => l.oneT === h).map((l) => orderedFacts.indexOf(l.manyT)).filter((i) => i >= 0);
    const at = orderedFacts.indexOf(h);
    if (at < 0 || !lines.length) continue;
    orderedFacts.splice(at, 1);
    orderedFacts.splice(Math.min(...lines.map((i) => (i > at ? i - 1 : i))), 0, h);
  }
  return { dims: row, facts: orderedFacts, iso, parent };
}

/** Each card's box in the matrix: lookup tables share one narrower width, everything else keeps its own. */
export function matrixSizer(tables, rels, /** @type {(name: string) => Size} */ own) {
  const { dims, facts } = matrixOrder(tables, rels);
  const w = matrixDimWidth(dims.length);
  const dimSet = new Set(dims), factSet = new Set(facts);
  return (/** @type {string} */ n) => {
    const b = own(n);
    return dimSet.has(n) ? { w, h: b.h } : factSet.has(n) ? { w: Math.max(MATRIX_FACT_W, b.w), h: MATRIX_FACT_H } : { w: Math.max(MATRIX_FACT_W, b.w), h: b.h };   // unlinked tables keep a full card
  };
}

/** @param {{ name: string, isDateTable?: boolean }[]} tables @param {any[]} rels @param {(name: string) => Size} sizeOf @returns {Map<string, XY>} */
export function matrixPositions(tables, rels, sizeOf) {
  const { dims, facts, iso, parent } = matrixOrder(tables, rels);
  /** @type {Map<string, XY>} */ const m = new Map();
  const factW = Math.max(MATRIX_FACT_W, ...facts.map((f) => sizeOf(f).w));
  const rowH = Math.max(NODE_H, ...dims.map((d) => sizeOf(d).h));
  const gap = matrixGap(dims.length);
  let x = factW + MATRIX_LEFT;
  dims.forEach((d, i) => { m.set(d, { x, y: 0 }); x += sizeOf(d).w + (dims[i + 1] !== undefined && parent[dims[i + 1]] === d ? MATRIX_CHILD_GAP : gap); });
  const right = dims.length ? x - gap : factW;
  let y = (dims.length ? rowH + MATRIX_TOP : 0);
  for (const f of facts) { m.set(f, { x: 0, y }); y += sizeOf(f).h + MATRIX_ROW_GAP; }
  if (iso.length) {   // under the facts, in rows as wide as the matrix
    const left = dims.length ? factW + MATRIX_LEFT : 0;   // under the first lookup table, clear of the zoom controls and the counts
    let cx = left, rowBottom = 0;
    y += MATRIX_ROW_GAP;
    for (const n of iso) {
      const b = sizeOf(n);
      if (cx > left && cx + b.w > Math.max(right, left + 640)) { cx = left; y += rowBottom + MATRIX_ROW_GAP; rowBottom = 0; }
      m.set(n, { x: cx, y }); cx += b.w + MATRIX_GAP; rowBottom = Math.max(rowBottom, b.h);
    }
  }
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
  // The matrix leaves a header's links into it out of "lookup table", so a header's own links to lookups are dots (matrixHeaders).
  const headers = layout === 'matrix' ? matrixHeaders(members, rels) : new Set();
  const brackets = layout === 'matrix' ? matrixMarks(members, rels) : new Map();
  const oneSides = new Set(rels.filter((r) => names.has(r.fromTable) && names.has(r.toTable) && !headers.has(roleOf(r).oneT)).map((r) => roleOf(r).oneT));
  const cells = new Set(rels.filter((r) => names.has(r.fromTable) && names.has(r.toTable) && !headers.has(roleOf(r).oneT) && !oneSides.has(roleOf(r).manyT)).map((r) => r.name));
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
    } else if (layout === 'busmatrix' || layout === 'matrix') {
      // Bus matrix (dims top, facts left): the fact (many-side) joins on its RIGHT, the dimension (one-side) connects
      // from its BOTTOM — an orthogonal L-route between the left fact column and the top dim row. A link between two
      // dimensions (an outrigger, which sits in the row beside the table it hangs off) runs side to side along the row:
      // the facing sides, left one's RIGHT to right one's LEFT (Kane, 8 Oct). With no positions yet, many RIGHT.
      const { manyT, oneT } = roleOf(r);
      const bracket = brackets.get(r.name);
      if (bracket) {   // a short mark or a tag on the open side (matrixMarks); never a long line
        fromSide = toSide = bracket.kind;
      } else if (oneSides.has(manyT) || headers.has(oneT)) {
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
      // The matrix: a fact's links all leave on one lane (its row) and a lookup table's all drop on one column, so every
      // link of a fact or lookup table shares ONE point. A link between two lookup tables keeps its own spread.
      const shared = layout === 'matrix' && (e.side === 'right' || e.side === 'bottom') && cells.has(e.rel);
      /** @type {Anchor} */ const a = { rel: e.rel, kind: e.kind, side: e.side, frac: shared ? 0.5 : (i + 1) / (g.length + 1) };
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
