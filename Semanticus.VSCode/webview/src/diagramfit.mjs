// Keeping Diagram cards clear of each other when their size changes (Kane, 25 Sep). A plain ES module (no React,
// no React Flow) so the node contract test (test/diagram-fit.test.mjs) executes the SAME code the webview ships,
// the storagecalc.mjs pattern.
import { demandSteps } from './diagramroute.mjs';
//
// The rule: when a card's height changes, every card BELOW it in the same visual column (their x ranges overlap)
// moves by exactly that change, down on expand and up on collapse. A width change does the same sideways for the
// cards to its right in the same row band. Both are measured against the geometry the positions were right for,
// so collapsing undoes expanding exactly. Anything still overlapping after that (a card dragged half under
// another, a card just added) is pushed straight down, just clear of the card above it plus CARD_GAP. Cards
// keep their x on a height change and their y on a width change, and a stack keeps its order.

/** The space left between a card and one it had to be pushed clear of (the Vertical layout's sibling gap). */
export const CARD_GAP = 48;
// The most pairs of places clearDropped looks at, nearest first, for a spot the person can see (the walk ends at the first one
// in view; only a layout with no clear spot in view goes on this long).
const DROP_LOOKS = 60000;

const spans = (a0, a1, b0, b1) => a0 < b1 && b0 < a1;

/**
 * @param cards  every card with its position and its NEW size: { id, x, y, w, h }
 * @param prev   the size each card had when its position was last right, by id. A card with no entry is
 *               treated as unchanged (it only takes part in the overlap pass).
 * @param gap    the clearance used when a card is pushed below an overlapping one
 * @param keep   ids the person or their assistant asked to keep where they are (a put-back, save_layout's keep): the
 *               overlap pass leaves them, though a card that grows above one still moves it by that growth
 * @returns      the new top-left of every card, by id
 */
export function reflowCards(cards, prev, gap = CARD_GAP, keep = null) {
  const old = (c) => prev[c.id] ?? { w: c.w, h: c.h };
  const dx = new Map(cards.map((c) => [c.id, 0]));
  const dy = new Map(cards.map((c) => [c.id, 0]));
  for (const c of cards) {
    const o = old(c);
    const dh = c.h - o.h, dw = c.w - o.w;
    if (!dh && !dw) continue;
    for (const d of cards) {
      if (d === c) continue;
      const od = old(d);
      if (dh && d.y > c.y && spans(c.x, c.x + o.w, d.x, d.x + od.w)) dy.set(d.id, dy.get(d.id) + dh);
      if (dw && d.x > c.x && spans(c.y, c.y + o.h, d.y, d.y + od.h)) dx.set(d.id, dx.get(d.id) + dw);
    }
  }
  const moved = cards.map((c, i) => ({ id: c.id, i, x: c.x + dx.get(c.id), y: c.y + dy.get(c.id), w: c.w, h: c.h }));
  // Overlap pass, top to bottom: each card is checked against the ones already settled above it and pushed down
  // until nothing covers it. Cards only ever move down here, so the loop ends.
  moved.sort((a, b) => a.y - b.y || a.x - b.x || a.i - b.i);
  // Kept cards stay put and settle first, so a card pushed down onto one goes on below it (review round 5).
  const settled = keep ? moved.filter((c) => keep.has(c.id)) : [];
  for (const c of moved) {
    if (keep?.has(c.id)) continue;
    for (;;) {
      const hits = settled.filter((s) => spans(s.x, s.x + s.w, c.x, c.x + c.w) && spans(s.y, s.y + s.h, c.y, c.y + c.h));
      if (!hits.length) break;
      c.y = Math.max(...hits.map((s) => s.y + s.h)) + gap;
    }
    settled.push(c);
  }
  return Object.fromEntries(moved.map((c) => [c.id, { x: c.x, y: c.y }]));
}

/**
 * Where to put the cards a person just dropped so none of them covers another card (review round 3 of m11/diagram-route:
 * a covered card hides its handles, and their lines then run behind the card on top) or, when `lines` is given (the
 * routed bus matrix), sits closer to a card it faces than the lines between them need (roomBetween; Kane, 28 Sep).
 * Only the dropped cards move: each to the nearest spot clear of every card, preferring one inside `view` (what the
 * person can see). A spot is beside a side of some card along x and beside a side of some card (or where it was let
 * go) along y, so a card can go round a corner: sliding only straight left, right, up or down sent a card dropped
 * among open cards 300 to 800 px away, out of view (review round 5), and only the 40 nearest places of each axis were
 * paired up, none of them flush with a card, so a spot corner to corner with one was missed (review round 6). Stepping out of the one card it covered was not
 * enough where cards sit close on every side: each way out landed on a neighbour and the card was saved on top of one
 * (review round 4). A spot beyond the card furthest right is always clear, so there is always one. Every other card
 * keeps the place the person or their assistant gave it (UAT-S11-1-004).
 * @param cards    every card with its position and size: { id, x, y, w, h }
 * @param dropped  the ids of the cards just dropped
 * @param gap      the clearance kept from the card a spot is beside (at least the room its lines need)
 * @param grid     when set (Snap), a spot lands on this grid, rounded away from the card it is beside
 * @param lines    how many lines leave each card by each side, by id (as makeRoom takes it), or null
 * @param view     the part of the canvas the person sees, in flow px ({ x, y, w, h }), or null
 * @returns        the new top-left of each dropped card that had to move, by id (no entry for one that did not)
 */
export function clearDropped(cards, dropped, gap = CARD_GAP, grid = 0, lines = null, view = null) {
  const k = (id, s) => (lines instanceof Map ? lines.get(id) : lines?.[id])?.[s] ?? 0;
  const need = (a, sa, b, sb) => (lines ? roomBetween(k(a.id, sa), k(b.id, sb)) : 0);
  // True when a and b overlap, or face each other closer than their lines need.
  const close = (a, b) => {
    const ox = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x), oy = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y);
    if (ox > 0 && oy > 0) return true;
    if (!lines) return false;
    if (oy > 0.5) { const [l, r] = a.x < b.x ? [a, b] : [b, a]; return r.x - (l.x + l.w) < need(l, 'right', r, 'left') - 0.01; }
    if (ox > 0.5) { const [t, u] = a.y < b.y ? [a, b] : [b, a]; return u.y - (t.y + t.h) < need(t, 'bottom', u, 'top') - 0.01; }
    return false;
  };
  const near = (v) => (grid ? Math.round(v / grid) * grid : v);
  const down = (v) => (grid ? Math.floor(v / grid) * grid : v), up = (v) => (grid ? Math.ceil(v / grid) * grid : v);
  const settled = cards.filter((c) => !dropped.includes(c.id));
  // The cards bucketed by cell, so a spot is checked against the few cards near it, not all of them (a walk over every
  // pair of places in a packed layout took up to 2 s at 150 open cards). A card can only be too close to a spot within
  // the most room any two of them need, so `reach` bounds where to look.
  let most = 0;
  if (lines) for (const o of cards) for (const s of ['top', 'right', 'bottom', 'left']) most = Math.max(most, k(o.id, s));
  const reach = lines ? roomBetween(most, most) : 0, CELL = 256, cellAt = new Map(), mark = [];
  let stamp = 0;
  const cellKey = (i, j) => i * 100003 + j;
  const each = (x0, y0, x1, y1, fn) => {
    for (let i = Math.floor(x0 / CELL); i <= Math.floor(x1 / CELL); i++) for (let j = Math.floor(y0 / CELL); j <= Math.floor(y1 / CELL); j++) if (fn(cellKey(i, j))) return true;
    return false;
  };
  const pool = [];
  const settle = (o) => {
    const at = pool.push(o) - 1;
    each(o.x, o.y, o.x + o.w, o.y + o.h, (key) => { let a = cellAt.get(key); if (!a) cellAt.set(key, a = []); a.push(at); });
  };
  settled.forEach(settle);
  const crowded = (spot) => {
    stamp++;
    return each(spot.x - reach, spot.y - reach, spot.x + spot.w + reach, spot.y + spot.h + reach, (key) => {
      for (const at of cellAt.get(key) ?? []) if (mark[at] !== stamp && (mark[at] = stamp) && close(spot, pool[at])) return true;
      return false;
    });
  };
  const out = {};
  for (const id of dropped) {
    const c = cards.find((o) => o.id === id);
    if (!c) continue;
    let spot = c;
    if (crowded(c)) {
      // Where the card could sit along each axis: where it was let go, just clear of a side of some card, and (with lines)
      // flush with a side, so it can sit corner to corner with a card it does not face (review round 6).
      const xs = new Set([near(c.x)]), ys = new Set([near(c.y)]);
      for (const o of settled) {
        xs.add(down(o.x - Math.max(gap, need(c, 'right', o, 'left')) - c.w)); xs.add(up(o.x + o.w + Math.max(gap, need(o, 'right', c, 'left'))));
        ys.add(down(o.y - Math.max(gap, need(c, 'bottom', o, 'top')) - c.h)); ys.add(up(o.y + o.h + Math.max(gap, need(o, 'bottom', c, 'top'))));
        if (lines) { xs.add(down(o.x - c.w)); xs.add(up(o.x + o.w)); ys.add(down(o.y - c.h)); ys.add(up(o.y + o.h)); }
      }
      // Every pair of places, nearest first (a walk over the two sorted lists with a heap, so only as many as it takes are
      // looked at): the nearest clear spot, and the nearest clear spot in view when there is one.
      const byX = [...xs].sort((a, b) => Math.abs(a - c.x) - Math.abs(b - c.x)), byY = [...ys].sort((a, b) => Math.abs(a - c.y) - Math.abs(b - c.y));
      const seen = (w) => !view || (w.x >= view.x && w.y >= view.y && w.x + c.w <= view.x + view.w && w.y + c.h <= view.y + view.h);
      // The distance, and a spot straight out along one axis before one round a corner at the same distance.
      const key = (i, j) => Math.hypot(byX[i] - c.x, byY[j] - c.y) + (byX[i] === near(c.x) || byY[j] === near(c.y) ? 0 : 1e-7);
      const heap = [], before = (p, q) => p.k < q.k;
      const push = (i, j) => {
        const e = { i, j, k: key(i, j) }; let n = heap.push(e) - 1;
        while (n > 0) { const up2 = (n - 1) >> 1; if (!before(e, heap[up2])) break; heap[n] = heap[up2]; n = up2; }
        heap[n] = e;
      };
      const pop = () => {
        const top = heap[0], last = heap.pop();
        if (heap.length) {
          let n = 0;
          for (;;) { let m = 2 * n + 1; if (m >= heap.length) break; if (m + 1 < heap.length && before(heap[m + 1], heap[m])) m++; if (!before(heap[m], last)) break; heap[n] = heap[m]; n = m; }
          heap[n] = last;
        }
        return top;
      };
      const queued = new Set([0]);
      const enqueue = (i, j) => { if (i < byX.length && j < byY.length && !queued.has(i * byY.length + j)) { queued.add(i * byY.length + j); push(i, j); } };
      push(0, 0);
      let free = null;
      const probe = { id: c.id, x: 0, y: 0, w: c.w, h: c.h };
      for (let looked = 0; heap.length && looked < DROP_LOOKS; looked++) {
        const { i, j } = pop();
        enqueue(i + 1, j); enqueue(i, j + 1);
        probe.x = byX[i]; probe.y = byY[j];
        if (crowded(probe)) continue;
        const w = { x: probe.x, y: probe.y };
        free ??= w;
        if (seen(w)) { free = w; break; }
      }
      // Beyond the card furthest right, clear of everything, when every nearer spot is taken.
      free ??= { x: up(Math.max(...settled.map((o) => o.x + o.w)) + Math.max(gap, ROOM_MIN)), y: near(c.y) };
      spot = { ...c, ...free };
    }
    settled.push(spot); settle(spot);
    if (spot.x !== c.x || spot.y !== c.y) out[id] = { x: spot.x, y: spot.y };
  }
  return out;
}

// Making room for the lines (Kane, 28 Sep). A layout can leave cards too close for the lines between them: an
// assistant's tidy grid 20 px apart, a card saved on top of or just under another, a person's drop among close
// neighbours, a card that grew. No routing can draw nine lines readably in a 20 px gap, and only the page knows each
// card's size and how many lines leave each side, so the page moves cards apart just enough and saves the moved
// spots, the way reflowCards does when a card opens.
// How much is enough: a side with k lines needs its end markers (9 px) and a first run per line, NEAR (3) px apart,
// beside it, and the far card kept ROOM_EDGE px clear. Every gap between two cards that face each other keeps at
// least ROOM_MIN, because the lines passing between two cards need room too, not only the lines that leave them.
// ROOM_MIN is measured, not derived: on the 80-table model in an assistant's grids (0, 10, 20 and 40 px apart, plain
// and star, three window sizes) a 48 px gap left no two lines within NEAR px and none on a card's border, while 40 px
// still left one to six pairs. 48 is also CARD_GAP, the gap the Vertical layout and a drop already keep.
/** px beside a card side for each line leaving it (the router keeps NEAR = 3 px between lines). */
export const ROOM_PER_LINE = 3;
/** px beside a side with lines for the end markers (9 px) before the first run. */
export const ROOM_MARKER = 9;
/** px a gap keeps clear of the card across it, once the lines leaving into it have their room. */
export const ROOM_EDGE = 4;
/** The narrowest gap left between two cards that face each other. */
export const ROOM_MIN = 48;

/** The gap two facing sides need, with kA and kB lines leaving them. */
export function roomBetween(kA, kB) {
  const side = (k) => (k > 0 ? ROOM_MARKER + ROOM_PER_LINE * k : 0);
  return Math.max(ROOM_MIN, side(kA) + side(kB) + ROOM_EDGE);
}

/**
 * Moves cards apart just enough that no two overlap and every gap between two facing cards has room for its lines.
 * Cards move only right and down, each only as far as a card before it pushes it, so a row or a column keeps its
 * order and anything already roomy stays where it is; a tidy grid stays a grid (the tidy step below). Two cards on
 * top of each other are parted along the axis that needs the smaller push.
 * @param cards  every card with its position and size: { id, x, y, w, h }
 * @param lines  how many lines leave each card by each side, by id: { top, right, bottom, left } (a Map or a record)
 * @param grid   when set (Snap), a card that moves lands on this grid, rounded on past the card that pushed it
 * @param keep   ids the person or their assistant asked to keep where they are: they never move here, so a put-back
 *               stays put; a card pushed toward one goes on past it (a card after one is still pushed as usual)
 * @param extra  px more than roomBetween that a pair needs, by 'left|right' or 'top|bottom' id (roomForLoads)
 * @returns      the new top-left of each card that moved, by id (no entry for one that did not)
 */
export function makeRoom(cards, lines, grid = 0, keep = null, extra = null) {
  const k = (id, s) => (lines instanceof Map ? lines.get(id) : lines?.[id])?.[s] ?? 0;
  const room = (a, sa, b, sb) => roomBetween(k(a.id, sa), k(b.id, sb)) + (extra?.get(a.id + '|' + b.id) ?? 0);
  const cur = cards.map((c) => ({ id: c.id, x: c.x, y: c.y, w: c.w, h: c.h }));
  const onGrid = (v) => (grid ? Math.ceil(v / grid - 1e-6) * grid : v);
  const mid = (c, ax) => c[ax] + (ax === 'x' ? c.w : c.h) / 2;
  // The push that parts a and b along x (a left of b, by their middles), and along y.
  const pushX = (a, b) => { const [l, r] = mid(a, 'x') <= mid(b, 'x') ? [a, b] : [b, a]; return l.x + l.w + roomBetween(k(l.id, 'right'), k(r.id, 'left')) - r.x; };
  const pushY = (a, b) => { const [t, u] = mid(a, 'y') <= mid(b, 'y') ? [a, b] : [b, a]; return t.y + t.h + roomBetween(k(t.id, 'bottom'), k(u.id, 'top')) - u.y; };
  // A kept card is a wall (review rounds 5 and 6): it never moves, so nothing ends on it or closer to it than their lines
  // need. A card pushed toward one goes on past it (right of it along x, below it along y) rather than onto it. That holds
  // across the other axis too: a card pushed into a kept card's column that ends only a few px above it goes on past it,
  // because nothing else would part the two (the kept card is not pushed, and the tidy step would line the card up onto
  // it). A card nothing pushed is left alone beside a kept card unless it lies on it (then it leaves by the shorter way,
  // like any two cards on each other) or sits before it, closer than their lines need, with lines leaving the sides that
  // face: the kept card cannot move away, so that card goes past it.
  const walls = cur.filter((c) => keep?.has(c.id));
  const pastKept = (b, ax, at) => {
    if (!walls.length) return at;
    const len = ax === 'x' ? 'w' : 'h', o = ax === 'x' ? 'y' : 'x', olen = ax === 'x' ? 'h' : 'w';
    const lo = ax === 'x' ? 'right' : 'bottom', hi = ax === 'x' ? 'left' : 'top';
    const olo = ax === 'x' ? 'bottom' : 'right', ohi = ax === 'x' ? 'top' : 'left';
    for (let hit = true; hit;) {
      hit = false;
      for (const w of walls) {
        const pushed = at > b[ax] + 0.01;
        const past = w[ax] + w[len] + room(w, lo, b, hi);
        if (Math.min(b[o] + b[olen], w[o] + w[olen]) - Math.max(b[o], w[o]) <= 0.5) {
          // Facing across the other axis: a card pushed into the kept card's span here, closer to it than their lines need.
          if (!pushed || Math.min(at + b[len], w[ax] + w[len]) - Math.max(at, w[ax]) <= 0.5) continue;
          const above = b[o] + b[olen] <= w[o] + 0.5;
          if ((above ? w[o] - (b[o] + b[olen]) : b[o] - (w[o] + w[olen])) >= (above ? room(b, olo, w, ohi) : room(w, olo, b, ohi)) - 0.01) continue;
          at = past; hit = true; continue;
        }
        if (!pushed && b[ax] + b[len] <= w[ax] + 0.5) {   // before the kept card, and nothing pushed it
          if (w[ax] - (b[ax] + b[len]) < room(b, lo, w, hi) - 0.01 && k(b.id, lo) + k(w.id, hi) > 0) { at = past; hit = true; }
          continue;
        }
        const pre = pushed ? room(w, lo, b, hi) : 0, post = pushed ? room(b, lo, w, hi) : 0;
        if (!(at < w[ax] + w[len] + pre - 0.5 && at + b[len] + post > w[ax] + 0.5)) continue;
        if (!pushed) {   // lying on the kept card: parted along this axis only when it is the shorter way
          const px = w.x + w.w + room(w, 'right', b, 'left') - b.x, py = w.y + w.h + room(w, 'bottom', b, 'top') - b.y;
          if (ax === 'x' ? px > py : py >= px) continue;
        }
        at = past;
        hit = true;
      }
    }
    return at;
  };
  for (let round = 0; round < 8; round++) {
    let moved = false;
    for (const ax of ['x', 'y']) {
      const len = ax === 'x' ? 'w' : 'h', o = ax === 'x' ? 'y' : 'x', olen = ax === 'x' ? 'h' : 'w';
      const lo = ax === 'x' ? 'right' : 'bottom', hi = ax === 'x' ? 'left' : 'top';
      // The pairs that face each other along this axis, read where the cards stand at the start of the pass: their
      // spans across overlap, and they do not overlap along it (or they do, and this axis parts them with less).
      const order = cur.slice().sort((a, b) => mid(a, ax) - mid(b, ax) || a[o] - b[o]);
      const at0 = new Map(order.map((c) => [c, { p: c[ax], q: c[o] }]));
      const before = new Map();
      for (let j = 0; j < order.length; j++) {
        const b = order[j], sb = at0.get(b);
        for (let i = 0; i < j; i++) {
          const a = order[i], sa = at0.get(a);
          if (Math.min(sa.q + a[olen], sb.q + b[olen]) - Math.max(sa.q, sb.q) <= 0.5) continue;
          if (Math.min(sa.p + a[len], sb.p + b[len]) - Math.max(sa.p, sb.p) > 0.5) {
            const px = pushX(a, b), py = pushY(a, b);
            if (ax === 'x' ? px > py : py >= px) continue;
          }
          let e = before.get(b); if (!e) before.set(b, e = []); e.push(a);
        }
      }
      for (const b of order) {
        if (keep?.has(b.id)) continue;
        let at = b[ax];
        for (const a of before.get(b) ?? []) at = Math.max(at, a[ax] + a[len] + room(a, lo, b, hi));
        at = pastKept(b, ax, at);
        if (at > b[ax] + 0.01) { b[ax] = onGrid(at); moved = true; }
      }
    }
    if (!moved) break;
  }
  // Tidy: cards that lined up before (a row of the assistant's grid, or a column) and were pushed by different
  // amounts join the one pushed furthest, so a grid does not go ragged under its taller cards. Only in a line the push
  // moved most of (a grid being spaced out, not one card nudged out of a row), and only where that makes no pair
  // tighter than its lines need: a card that would crowd another (or come to crowd a kept card) stays where the push left it.
  const tight = (a, b) => {
    const ox = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x), oy = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y);
    if (ox > 0.5 && oy > 0.5) return true;
    if (oy > 0.5) { const [l, r] = a.x <= b.x ? [a, b] : [b, a]; return r.x - (l.x + l.w) < room(l, 'right', r, 'left') - 0.01; }
    if (ox > 0.5) { const [t, u] = a.y <= b.y ? [a, b] : [b, a]; return u.y - (t.y + t.h) < room(t, 'bottom', u, 'top') - 0.01; }
    return false;
  };
  const crowds = (c) => { const n = new Set(); for (const d of cur) if (d !== c && tight(c, d)) n.add(d); return n; };
  for (const ax of ['x', 'y']) {
    const lined = new Map();
    cur.forEach((c, i) => { const key = Math.round(cards[i][ax] * 2); let g = lined.get(key); if (!g) lined.set(key, g = []); g.push(c); });
    const start = new Map(cur.map((c, i) => [c, cards[i][ax]]));
    for (const g of lined.values()) {
      if (g.length < 2 || 2 * g.filter((c) => c[ax] !== start.get(c)).length < g.length) continue;
      const to = Math.max(...g.map((c) => c[ax]));
      for (const c of g) {
        if (keep?.has(c.id) || c[ax] >= to - 0.01) continue;
        const was = c[ax], before = crowds(c);
        c[ax] = to;
        for (const d of crowds(c)) if (!before.has(d) || keep?.has(d.id)) { c[ax] = was; break; }   // never tighter on a kept card
      }
    }
  }
  const out = {};
  cur.forEach((c, i) => { if (c.x !== cards[i].x || c.y !== cards[i].y) out[c.id] = { x: c.x, y: c.y }; });
  return out;
}

/** px a gap needs for each line that passes through it (NEAR and a px), and px kept clear of the cards on each side.
 *  ROOM_CLEAR is measured, like ROOM_MIN (review round 5): over 210 assistant grids (1 to 300 tables, plain 0 to 40 px
 *  and star 20 to 80 px, three windows), roomSteps with 10 px left a line on a card's border in 2 of them, 16 px in
 *  none but one 200-table grid, and 20 px in none. */
export const ROOM_PASSING = ROOM_PER_LINE + 1;
export const ROOM_CLEAR = 20;
/** How many times roomSteps widens the gaps its lines' demand still crowds. */
export const ROOM_ROUNDS = 2;
/**
 * How many lines pass through each gap between two cards that face each other, read off routes (routeAll's, or
 * demandSteps' shortest ones): a line counts for a gap when one of its runs lies inside it, along it for over 4 px of
 * the span the two cards share. Lines passing between two cards need room as well as the lines that leave them, so
 * roomSteps asks each busy gap for room for all of them (roomForLoads) and makes room again, a few rounds at most.
 * @param cards   every card's box with its id: { id, x, y, w, h }, in the routes' coordinates
 * @param routes  Map id -> { pts }
 * @returns       Map 'left|right' or 'top|bottom' id -> how many lines pass through that gap
 */
export function gapLoads(cards, routes) {
  // Cards bucketed by BAND px along each axis, so the cards beside a point are found without a walk over all of them.
  const BAND = 64;
  const index = (q, lq) => {
    const m = new Map();
    for (const c of cards) for (let k = Math.floor(c[q] / BAND); k <= Math.floor((c[q] + c[lq]) / BAND); k++) { let a = m.get(k); if (!a) m.set(k, a = []); a.push(c); }
    return m;
  };
  const byY = index('y', 'h'), byX = index('x', 'w');
  const sets = new Map();
  for (const [id, r] of routes) {
    const pts = r?.pts;
    if (!pts) continue;
    for (let i = 1; i < pts.length; i++) {
      const a = pts[i - 1], b = pts[i], v = a.x === b.x;
      if (!v && a.y !== b.y) continue;
      const p = v ? 'x' : 'y', q = v ? 'y' : 'x', lp = v ? 'w' : 'h', lq = v ? 'h' : 'w', idx = v ? byY : byX;
      const at = a[p], lo = Math.min(a[q], b[q]), hi = Math.max(a[q], b[q]);
      if (hi - lo <= 4) continue;
      // The gap at points along the run, BAND / 2 apart: the nearest card before the run and after it, both beside
      // that point. Each gap the run passes through counts the line once.
      for (let t = lo + 2; t < hi - 2 + BAND / 2; t += BAND / 2) {
        const y = Math.min(t, hi - 2);
        let L = null, R = null;
        for (const c of idx.get(Math.floor(y / BAND)) ?? []) {
          if (y < c[q] || y > c[q] + c[lq]) continue;
          if (c[p] + c[lp] <= at + 0.01) { if (!L || c[p] + c[lp] > L[p] + L[lp]) L = c; }
          else if (c[p] >= at - 0.01) { if (!R || c[p] < R[p]) R = c; }
        }
        if (!L || !R) continue;
        const key = L.id + '|' + R.id;
        let set = sets.get(key); if (!set) sets.set(key, set = new Set());
        set.add(id);
      }
    }
  }
  return new Map([...sets].map(([k, set]) => [k, set.size]));
}

/**
 * The extra room each busy gap needs over roomBetween (makeRoom's `extra`): its end markers, ROOM_PASSING px for
 * every line through it and ROOM_CLEAR px clear of the cards, where that is more than the gap already gets.
 * @param loads  gapLoads' result
 * @param lines  how many lines leave each card by each side, by id (as makeRoom takes it)
 * @param axisOf a pair's axis: 'x' when its cards sit side by side, 'y' when one is above the other
 * @param prev   the extra already asked for (kept where it is larger: a gap never narrows between rounds)
 * @returns      Map pair -> px
 */
export function roomForLoads(loads, lines, axisOf, prev = null) {
  const k = (id, s) => (lines instanceof Map ? lines.get(id) : lines?.[id])?.[s] ?? 0;
  const out = new Map(prev ?? []);
  for (const [key, n] of loads) {
    const [a, b] = key.split('|'), x = axisOf(key) === 'x';
    const more = ROOM_MARKER + ROOM_PASSING * n + ROOM_CLEAR - roomBetween(k(a, x ? 'right' : 'bottom'), k(b, x ? 'left' : 'top'));
    if (more > (out.get(key) ?? 0)) out.set(key, more);
  }
  return out;
}

// Where a line end sits: `frac` of the way along its card's side, 4 px out (where React Flow draws the handle).
const endOn = (c, e) => (e.side === 'top' ? { x: c.x + e.frac * c.w, y: c.y - 4, side: 'top' }
  : e.side === 'bottom' ? { x: c.x + e.frac * c.w, y: c.y + c.h + 4, side: 'bottom' }
  : e.side === 'left' ? { x: c.x - 4, y: c.y + e.frac * c.h, side: 'left' } : { x: c.x + c.w + 4, y: c.y + e.frac * c.h, side: 'right' });

/**
 * Making room for the lines, all of it in one go (review round 5). makeRoom first; then, where it moved anything, the
 * lines passing between cards: two cheap routings of every line (demandSteps) say which gaps it passes through, each
 * busy gap is asked for room for the most lines either sends through it (gapLoads, roomForLoads) and makeRoom runs
 * again, ROOM_ROUNDS times at most or until nothing more moves. The page moves the cards once, to the end result, and
 * routes that once.
 * Why not from the drawn routes: a routed pass of a 300-table grid takes seconds, and widening the gaps the drawn lines
 * crowded and routing again, up to five times, froze the page for 40 s at a time and kept it moving cards for two
 * minutes; which rounds ran also depended on when each pass landed, so the same save ended in different layouts
 * (review round 4). The cheap routings depend only on the cards, so the result is the same every time for the same
 * layout. Why two: each line on its own way sends every line down one of two equally short side-by-side gaps, while a
 * full pass shares them out, so the other came out too narrow and its lines were drawn on a card's border; routed
 * against each other on one track the lines share out too far, and the main channel came out too narrow for the full
 * pass's tracks, which then tried every track for every line (a 300-table pass four times as long). The most either
 * asks for has room for both.
 * A generator that yields between searches, so the page can run it a few ms at a time (drain() runs it to the end).
 * @param cards  every card with its position and size: { id, x, y, w, h }
 * @param links  every line and the ends it joins: { id, from: { id, side, frac }, to: { id, side, frac } }, frac being
 *               how far along its card's side the end sits (0 to 1)
 * @param lines  how many lines leave each card by each side, by id (as makeRoom takes it)
 * @param grid   Snap's grid, or 0 (as makeRoom)
 * @param keep   ids never moved (as makeRoom)
 * @returns      the new top-left of each card that moved, by id (no entry for one that did not)
 */
export function* roomSteps(cards, links, lines, grid = 0, keep = null) {
  const cur = cards.map((c) => ({ id: c.id, x: c.x, y: c.y, w: c.w, h: c.h }));
  const apply = (to) => { for (const c of cur) { const p = to[c.id]; if (p) { c.x = p.x; c.y = p.y; } } };
  const first = makeRoom(cur, lines, grid, keep);
  if (!Object.keys(first).length) return {};   // a layout with room is left alone, lines and all
  apply(first);
  let extra = new Map();
  for (let round = 0; round < ROOM_ROUNDS; round++) {
    const by = new Map(cur.map((c) => [c.id, c]));
    const ends = [];
    for (const l of links) { const a = by.get(l.from.id), b = by.get(l.to.id); if (a && b && a !== b) ends.push({ id: l.id, src: endOn(a, l.from), dst: endOn(b, l.to) }); }
    const loads = gapLoads(cur, yield* demandSteps(ends, cur));
    for (const [k, n] of gapLoads(cur, yield* demandSteps(ends, cur, true))) if (n > (loads.get(k) ?? 0)) loads.set(k, n);
    // A pair faces across x when its cards share some height (side by side), else across y.
    const axisOf = (pair) => { const [a, b] = pair.split('|').map((id) => by.get(id)); return Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y) > 0 ? 'x' : 'y'; };
    extra = roomForLoads(loads, lines, axisOf, extra);
    const to = makeRoom(cur, lines, grid, keep, extra);
    if (!Object.keys(to).length) break;
    apply(to);
  }
  const out = {};
  cur.forEach((c, i) => { if (c.x !== cards[i].x || c.y !== cards[i].y) out[c.id] = { x: c.x, y: c.y }; });
  return out;
}

/**
 * A Put back stays put (review round 7, the coordinator's ruling on round 6). Put back is the person saying no, so a fresh
 * page on that layout must move nothing, including a card the page never moved that touches a card the Put back keeps
 * (the settle pass moves such a card past the kept one, so the kept card has room for its lines). This finds those cards:
 * it runs the same settle pass a fresh page runs (reflowCards, then roomSteps) with `keep`, adds every card that pass
 * would move to the kept set (each stays where it is), and runs it again, until a pass has nothing left to move.
 * A card grows the set every round, so it ends. The cards are the person's layout from then on: the page keeps them
 * where they are, saved with a keep mark, and makes room again only when the layout changes.
 * @param cards     every card with its position and size: { id, x, y, w, h }
 * @param linksFor  the links roomSteps takes, worked out from the boxes (their ends depend on where the cards are)
 * @param lines     as makeRoom takes it
 * @param grid      Snap's grid, or 0
 * @param keep      the ids the Put back kept (the cards the page moved, back where they were)
 * @returns         the ids added to `keep`, in the order found (a Set), none of them in `keep` already
 */
export function* keepBeside(cards, linksFor, lines, grid = 0, keep = null) {
  const kept = new Set(keep ?? []), added = new Set();
  const same = Object.fromEntries(cards.map((c) => [c.id, { w: c.w, h: c.h }]));
  for (;;) {
    const grown = reflowCards(cards, same, CARD_GAP, kept);
    const boxes = cards.map((c) => ({ id: c.id, ...grown[c.id], w: c.w, h: c.h }));
    const room = yield* roomSteps(boxes, linksFor(boxes), lines, grid, kept);
    const more = cards.filter((c) => {
      if (kept.has(c.id)) return false;
      const to = room[c.id] ?? grown[c.id];
      return to.x !== c.x || to.y !== c.y;
    }).map((c) => c.id);
    if (!more.length) return added;
    for (const id of more) { kept.add(id); added.add(id); }
  }
}

/** How many lines leave a card by each side, from its anchors ({ side } each, side 'top' | 'right' | 'bottom' | 'left'). */
export function linesBySide(anchors) {
  const n = { top: 0, right: 0, bottom: 0, left: 0 };
  for (const a of anchors ?? []) if (a.side in n) n[a.side]++;
  return n;
}

/** Open or close one table. Closed tables carry no entry, so the map only lists what is open. */
export function toggleExpansion(expanded, name) {
  const { [name]: was, ...rest } = expanded;
  return was ? rest : { ...rest, [name]: true };
}

/** Expand all / Collapse all: changes only the named (shown) tables and keeps every other table as it was. */
export function setExpansionFor(expanded, names, on) {
  const next = { ...expanded };
  for (const n of names) { if (on) next[n] = true; else delete next[n]; }
  return next;
}

/**
 * Looking is not a layout change (review round 8, the coordinator's ruling). Opening or closing a card's columns, one card
 * or all of them, zooming, panning and selecting never move another card, save or toast; an opened card is drawn on top of
 * its neighbours while it is open. So the page settles cards by each card's CLOSED box, the box its anchors give it (the
 * card is rendered at exactly that width and at least that height), never by how tall it happens to be drawn. Only a real
 * change of that box (a line added to a side of the card, so the card needs more room) counts as a size change.
 * @param cards  every card's closed box: { id, w, h } (extra fields, such as an open card's measured size, are ignored)
 * @param last   the boxes at the previous look, by id (null the first time)
 * @returns      `prev`: the previous box of every card seen before, for reflowCards; `changed`: some card's box changed
 */
export function boxChanges(cards, last) {
  const prev = {};
  let changed = false;
  for (const c of cards) {
    const was = last?.get(c.id);
    if (!was) continue;
    prev[c.id] = { w: was.w, h: was.h };
    if (Math.abs(was.w - c.w) > 0.5 || Math.abs(was.h - c.h) > 0.5) changed = true;
  }
  return { prev, changed };
}

/**
 * True when rebuilding the cards is a change of the LAYOUT, so the overlap pass has to run afterwards: another diagram, the
 * tables on it, the layout mode or a reloaded model. Opening a card or a column list arriving rebuilds the cards too (to
 * show the columns), and is not (`was` and `now` are the values the rebuild depends on; `was` is null on the first one).
 */
export function isLayoutChange(was, now) {
  return !was || was.graph !== now.graph || was.activeId !== now.activeId || was.membershipKey !== now.membershipKey || was.layout !== now.layout;
}

/**
 * The boxes the settle pass compares the cards' new boxes with. An arrange (Vertical, Layered, Bus matrix) places every card with
 * the box of the TARGET layout, so those positions are already right for the new boxes: comparing them with the boxes of the
 * layout before would count the change a second time and shift every card right of, or below, a card whose box changed. A
 * layout mode that changes on the same diagram therefore compares with nothing. Anything else (a card opened, a reloaded model,
 * another diagram) compares with the last boxes as before.
 */
export function sizesToCompare(was, now, last) {
  return was && was.activeId === now.activeId && was.layout !== now.layout ? new Map() : last;
}

/**
 * Of the changes isLayoutChange counts, the ones whose make-room runs at once even while cards are open (round 9): a page's
 * first rebuild, another diagram, the tables on it, or the layout mode. A reloaded model alone (a link was made) is the one that
 * waits: while a spread is drawn the page's own make-room runs once, when the spread ends.
 */
export function isArrangeChange(was, now) {
  return !was || was.activeId !== now.activeId || was.membershipKey !== now.membershipKey || was.layout !== now.layout;
}

/**
 * The spread (Kane, 29 Sep, "Slide down"): opening a card is a VIEW, not a layout change. While any card is open the page draws
 * the cards where the layout would have to put them to make space for the open ones, on screen only, worked out from the
 * SAVED layout plus the set of open cards, so the same open cards always draw the same spread and closing them puts every card
 * back on its saved spot. This is the first half of it, the pass the page ran before review round 8 (the cards below an open
 * card move down by how much it grew, then anything still covering another is pushed clear); the second half is roomSteps,
 * which gives the lines their room. Nothing here is ever saved.
 * @param cards  every card: { id, x, y (its SAVED spot), w, h (the size it is drawn at), bw, bh (its closed box), open }
 * @param keep   ids kept where they are (as reflowCards)
 * @param gap    the clearance used when a card is pushed below an overlapping one
 * @returns      `needed`: some open card is drawn bigger than its closed box, so there is something to spread for;
 *               `grown`: the drawn spot of every card after the growth pass (the saved spots when nothing is needed)
 */
export function spreadStart(cards, keep = null, gap = CARD_GAP) {
  const needed = cards.some((c) => c.open && (Math.abs(c.w - c.bw) > 0.5 || Math.abs(c.h - c.bh) > 0.5));
  if (!needed) return { needed, grown: Object.fromEntries(cards.map((c) => [c.id, { x: c.x, y: c.y }])) };
  // An open card grew from its closed box; a card that is closed did not grow, however tall its own text draws it.
  const prev = Object.fromEntries(cards.map((c) => [c.id, c.open ? { w: c.bw, h: c.bh } : { w: c.w, h: c.h }]));
  return { needed, grown: reflowCards(cards.map((c) => ({ id: c.id, x: c.x, y: c.y, w: c.w, h: c.h })), prev, gap, keep) };
}

/**
 * The saved spot of every card while a spread is drawn (review round 11, R10-1). While cards are open the flow store holds the
 * spots the spread DREW, so the saved ones live here and nowhere else, kept apart from the spread's own state on purpose: round 10
 * kept them inside it, a guard in the spread's `draw` cleared that state, and the rebuild that followed took the drawn spots for
 * the saved layout and saved some of them. Nothing the spread does (its guards, its late results, a new picture replacing the
 * old) holds, reads or clears this; only a page-level event does, and it puts the store back on what `release` returns (or
 * places every card from `peek`, as the rebuild does). Held means the store may not be on the saved spots.
 */
export function savedSpots() {
  let held = null;
  return {
    /** The first saved layout held is kept: a further spread over the same cards never replaces it. */
    hold(saved) { if (!held) held = { ...saved }; },
    /** Every card's saved spot, or null when the store already holds them. */
    peek() { return held; },
    /** A drag while spread moved these cards' saved spots. */
    amend(part) { if (held) held = { ...held, ...part }; },
    /** The spread is over: hands back the saved spots, and the caller puts every card on them. */
    release() { const was = held; held = null; return was; },
  };
}

/**
 * What a spread was drawn from, as one string: two looks with the same key draw the same spread. It is the open cards, the size
 * every card is drawn at, and whatever else the spread depends on (`extra`: the layout mode, Snap, the kept cards).
 * @param cards  every card: { id, w, h, open } (the size it is drawn at)
 * @param extra  anything else the result depends on, as a string
 */
export function spreadKey(cards, extra = '') {
  return extra + '|' + cards.map((c) => `${c.id}${c.open ? '*' : ''}:${c.w.toFixed(1)}x${c.h.toFixed(1)}`).join(',');
}

/**
 * A drag while spread (ruling item 4): the card's SAVED spot moves by the distance it was dragged, from where it was drawn to
 * where it was let go, and the spread's own shift of it stays out of what is saved.
 * @param saved      the card's saved spot before the drag
 * @param drawnFrom  where the card was drawn when the drag began
 * @param droppedAt  where the card was let go (drawn spot)
 */
export function savedAfterDrag(saved, drawnFrom, droppedAt) {
  return { x: saved.x + droppedAt.x - drawnFrom.x, y: saved.y + droppedAt.y - drawnFrom.y };
}

/**
 * True when a change the engine announced can change what the Diagram draws (its tables or its lines): a structure, add or
 * remove delta, or a walk of the timeline. A redo of an added relationship carries no delta at all (TOM fires no event for
 * an add, and only an edit's commit reads the added objects), so a walk reloads whatever its deltas say (review round 10, L3).
 * A notification with no delta list at all reloads too, as before. A pure property update does not.
 */
export function changeReloadsDiagram(n) {
  if (n?.replay === 'undo' || n?.replay === 'redo') return true;
  return n?.deltas?.some((d) => d.kind === 'structure' || d.kind === 'add' || d.kind === 'remove') ?? true;
}

/**
 * The box a layout reserves for a card: its anchor box, grown to the card's measured size when it is bigger and the card is
 * open (an open card draws its columns, taller than its anchor box). A closed card's measured size belongs to the layout it was
 * drawn in, so arranging into another layout would read the old layout's box as this one's (Layered, then Bus matrix, left the
 * cards wider apart than a fresh bus matrix and some of them shifted again when the cards were measured).
 */
export function layoutBox(anchorBox, measured, open = true) {
  if (!open) return { w: anchorBox.w, h: anchorBox.h };
  return { w: Math.max(anchorBox.w, measured?.width ?? 0), h: Math.max(anchorBox.h, measured?.height ?? 0) };
}

// Typing in an open card's column filter must never move other cards (Kane, 25 Sep). The card records its height
// while it is open with an EMPTY filter, and while a filter is active it keeps at least that height, so a shorter
// filtered list leaves the card's box, and so every other card, exactly where it was.
/** True when the card's current height is its own (open, no filter) and should be remembered. */
export function recordsHeight(expanded, filter) {
  return !!expanded && !filter.trim();
}
/** The minimum height to hold an open, filtered card at, or undefined when it should size itself. */
export function filterHeldHeight(expanded, filter, held) {
  return expanded && filter.trim() && held ? held : undefined;
}

// The Diagram's first frame (journey m11 F17). Fitting a whole bus matrix into a Studio about 790px wide put the
// card text at 3 to 4 screen px. The first frame keeps the text readable instead: when the whole diagram only fits
// below this zoom, the frame holds this zoom and starts at the diagram's top-left corner (where the bus matrix puts
// the facts). Fit still shows everything, on request. 0.85 keeps the cards' smallest text (11px) at 9 screen px.
export const FIRST_FIT_MIN_ZOOM = 0.85;

/**
 * @param bounds      the cards' bounding box in flow coordinates: { x, y, width, height }
 * @param width       the canvas width in screen px
 * @param height      the canvas height in screen px
 * @param fittedZoom  the zoom a full fit would use
 * @param pad         screen px left clear at the edge the frame starts from
 * @returns           null when the full fit is readable (use it), else the { x, y, zoom } to show
 */
export function readableFirstView(bounds, width, height, fittedZoom, pad, minZoom = FIRST_FIT_MIN_ZOOM) {
  if (!(width > 0) || !(height > 0) || fittedZoom >= minZoom) return null;
  const z = minZoom;
  // An axis that fits at this zoom is centred on it; one that does not starts at its top or left edge.
  const along = (start, size, room) => (size * z + 2 * pad <= room ? (room - size * z) / 2 - start * z : pad - start * z);
  return { x: along(bounds.x, bounds.width, width), y: along(bounds.y, bounds.height, height), zoom: z };
}

/**
 * The positions a save should carry: only the cards whose position actually changed (UAT-S11-1-004). Saving every
 * card on a drag wrote the canvas's own arrangement over the other tables' stored positions, so the engine layout
 * lost six tables' positions for one moved table.
 * @param before  the positions the cards had, by id
 * @param after   the positions they have now, by id
 * @returns       the entries of `after` that differ from `before` (a card missing from `before` counts as moved)
 */
export function changedPositions(before, after) {
  const out = {};
  for (const [id, p] of Object.entries(after)) {
    const was = before[id];
    if (!was || Math.abs(was.x - p.x) > 0.5 || Math.abs(was.y - p.y) > 0.5) out[id] = p;
  }
  return out;
}
