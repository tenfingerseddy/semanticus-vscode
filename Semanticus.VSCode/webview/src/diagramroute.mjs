// Routing a relationship line through the gaps between Diagram cards (the space rule, 27 Sep). A plain ES module
// (no React, no React Flow) so test/diagram-route.test.mjs executes the SAME code the webview ships.
//
// Why: a line must never run behind a card, whatever spot a drag or the person's assistant leaves the cards on, so
// each line is found on a grid of the channels between cards. (The bus matrix wrapped its lookup tables into rows
// from 27 Sep to 8 Oct; Kane put them back in one row, where most lines are a plain L, see busGrid.)
// The search is A* with a cost per bend, so a line takes the fewest turns it can. A route never enters any card's
// box grown by the margin, including the two cards it joins: it leaves each one along a short straight stub first.
//
// Tracks (Kane's rule that every line gets its own lane): a channel is the free gap beside a card's side, and a track
// is a fixed FRACTION of that gap (the middle first, then the quarters, then the eighths). A line routed on track t
// may only turn on track-t lines, so two lines on different tracks can never run along the same line inside a
// channel, however narrow the channel is. routeAll hands out the tracks: each line takes the lowest track that does
// not run along a line already placed.
//
// Close cards (a drag, or a layout the person's assistant saved): the margin shrinks before a line gives up (10, then
// 4, then 1, then 0 px), so a line squeezes through a narrow gap rather than falling back to a line through a card.
//
// Busy channels (review round 3): a channel can carry fewer lines on their own tracks than want to use it (a tidy grid
// an assistant saves, 20 or 40 px apart, or every card open on a big model), and tracks of two channels can fall
// within NEAR px of each other. So once every line is placed, spread() slides a run of each line still drawn along
// another sideways inside its channel to the nearest free place, and a line that still shares is routed again at
// every margin, against everything else already placed, before a last spread.
//
// No router can draw nine lines readably in a 20 px gap (review round 3). So the page makes room first (Kane, 28 Sep:
// diagramfit.mjs makeRoom moves cards apart until every gap has room for its lines, ROOM_MIN at least), and this file
// only has to share out channels that have room. What it does in a gap with no room (a card the person kept there)
// is the least bad thing, not a clean drawing: see TIGHT and HUG.

/** Clearance kept between a line and every card, in flow px, while the gap allows it. */
export const MARGIN = 10;
/** The smaller clearances tried, in order, when cards sit too close for the full one. */
export const MARGINS = [MARGIN, 4, 1, 0];
/** How far a line may sit from a card side that faces no other card (the gap a lone side is given). */
export const OPEN = 40;
/** Two lines closer than this, running the same way for more than RUN px, count as drawn on top of each other. */
export const NEAR = 3;
export const RUN = 4;
/** A run within this of another line weighs 1000 times one within NEAR, so the spread parts such lines first. Where
 *  a gap is too narrow even for that (a slit a few px wide), lines can still end closer: the page makes room so a
 *  layout it draws never has such a gap, unless the person kept the cards there. */
export const TIGHT = 2;
/** A run closer than this to a card's border, along it for more than RUN px, is drawn on the border. */
export const HUG = 2;
// What a px of run on a border costs the spread, against 1 for a px within NEAR of another line and 1000 within TIGHT.
// Measured over the verifier's 181 live layouts with room made: 500 left one run on a border, 100 left eleven.
const HUG_WEIGHT = 500;
/** The narrowest free gap left open at a margin: room for two tracks NEAR px apart either side of the middle. */
export const MIN_FREE = 2 * NEAR;
const BEND = 40;   // one turn costs as much as 40 px of line
const TRIES = 16;  // tracks routeAll tries for one line before it keeps the least-shared route (a clean one ends it early)
const SHARE = 8;   // each px run along a placed line costs this much more than a free px
const WINDOW = 320; // px a search may stray outside its two ends' rectangle before it retries over the whole canvas
const REROUTE = 0.5;       // routing a sharing line again may cost at most this share of the first pass's search work,
const REROUTE_MIN = 1e5;   // or this many search steps on a small model, so a hopelessly packed layout cannot freeze
const SPREAD_REACH = 64;   // px a run may slide sideways
const SPREAD_ROUNDS = 6;

const DIR = { top: [0, -1], bottom: [0, 1], left: [-1, 0], right: [1, 0] };

// A measured card edge carries float noise (a card saved 4 px beside another put the handle 0.00001 px inside it), so
// a point within EPS of a border is on the border, not inside.
const EPS = 0.01;
/** True when the segment a-b (horizontal or vertical) passes through the inside of box r (more than EPS inside). */
export function segmentHitsBox(a, b, r) {
  const x0 = Math.min(a.x, b.x), x1 = Math.max(a.x, b.x), y0 = Math.min(a.y, b.y), y1 = Math.max(a.y, b.y);
  if (x0 === x1) return x0 > r.x + EPS && x0 < r.x + r.w - EPS && y0 < r.y + r.h - EPS && y1 > r.y + EPS;
  if (y0 === y1) return y0 > r.y + EPS && y0 < r.y + r.h - EPS && x0 < r.x + r.w - EPS && x1 > r.x + EPS;
  return false;
}
const covers = (b, p) => p.x > b.x + EPS && p.x < b.x + b.w - EPS && p.y > b.y + EPS && p.y < b.y + b.h - EPS;

/** Where track t sits across a gap, as a fraction: 1/2, 1/4, 3/4, 1/8, 3/8... (van der Corput), all different. */
export function trackFraction(t) {
  let n = Math.max(0, Math.floor(t)) + 1, f = 0, d = 0.5;
  while (n) { if (n & 1) f += d; n >>= 1; d /= 2; }
  return f;
}

// The boxes grown by one margin, and the free gap beside each of their four sides: the distance to the nearest other
// grown box facing that side (overlapping it across), or OPEN when nothing faces it. Worked out once per box set.
// A slit between two facing boxes narrower than MIN_FREE has no room for two tracks, so every track would fall on
// its one middle line and the lines through it would be drawn on top of each other: at this margin it is plugged
// (an extra box fills it), which sends those lines to a smaller margin, where the slit is wider, or another way.
// The last margin (0) plugs nothing: a line squeezed through a slit beats a line drawn through a card.
function sides(boxes, margin) {
  const g = boxes.map((b) => ({ x: b.x - margin, y: b.y - margin, w: b.w + 2 * margin, h: b.h + 2 * margin }));
  const gap = g.map(() => ({ l: Infinity, r: Infinity, t: Infinity, b: Infinity }));
  for (let i = 0; i < g.length; i++) {
    const a = g[i], ga = gap[i];
    for (let j = 0; j < g.length; j++) {
      if (i === j) continue;
      const b = g[j];
      if (b.y < a.y + a.h && b.y + b.h > a.y) {           // side by side
        if (b.x >= a.x + a.w) ga.r = Math.min(ga.r, b.x - (a.x + a.w));
        else if (b.x + b.w <= a.x) ga.l = Math.min(ga.l, a.x - (b.x + b.w));
      }
      if (b.x < a.x + a.w && b.x + b.w > a.x) {           // one above the other
        if (b.y >= a.y + a.h) ga.b = Math.min(ga.b, b.y - (a.y + a.h));
        else if (b.y + b.h <= a.y) ga.t = Math.min(ga.t, a.y - (b.y + b.h));
      }
    }
  }
  for (const q of gap) for (const k of ['l', 'r', 't', 'b']) if (q[k] === Infinity) q[k] = OPEN;
  const n = margin > 0 ? g.length : 0;
  for (let i = 0; i < n; i++) for (let j = 0; j < n; j++) {
    if (i === j) continue;
    const a = g[i], b = g[j];
    const d = b.x - (a.x + a.w), e = b.y - (a.y + a.h);
    if (d >= 0 && d < MIN_FREE && gap[i].r === d && b.y < a.y + a.h && b.y + b.h > a.y) {
      const y0 = Math.max(a.y, b.y), y1 = Math.min(a.y + a.h, b.y + b.h);
      g.push({ x: a.x + a.w - 1, y: y0, w: d + 2, h: y1 - y0 }); gap.push({ l: 0, r: 0, t: OPEN, b: OPEN });
    }
    if (e >= 0 && e < MIN_FREE && gap[i].b === e && b.x < a.x + a.w && b.x + b.w > a.x) {
      const x0 = Math.max(a.x, b.x), x1 = Math.min(a.x + a.w, b.x + b.w);
      g.push({ x: x0, y: a.y + a.h - 1, w: x1 - x0, h: e + 2 }); gap.push({ l: OPEN, r: OPEN, t: 0, b: 0 });
    }
  }
  return { margin, grown: g, gap, lines: new Map() };
}

// The track lines beside every side of every grown box, sorted: worked out once per margin and track and shared by
// every search there (sorting them in each search was most of a pass's time on a big model). Facing sides of two
// cards give the same line, because both measure it from the left (or top) box: a.right + f * gap === b.left - (1 - f) * gap.
function trackLines(grown, gap, f) {
  const xs = [], ys = [];
  for (let i = 0; i < grown.length; i++) {
    const r = grown[i], q = gap[i];
    xs.push(r.x - (1 - f) * q.l, r.x + r.w + f * q.r);
    ys.push(r.y - (1 - f) * q.t, r.y + r.h + f * q.b);
  }
  return { X: uniq(xs), Y: uniq(ys) };
}
// The sorted lines inside [lo, hi], with a search's two stub ends added.
function windowOf(A, lo, hi, a, b) {
  const out = A.slice(firstAtOrAbove(A, lo), firstAbove(A, hi));
  for (const v of [a, b]) { const i = firstAtOrAbove(out, v); if (out[i] !== v) out.splice(i, 0, v); }
  return out;
}

// Search steps taken, so routeAll can bound the work it spends routing sharing lines again.
const work = { steps: 0 };

/** Everything about a set of cards the router needs, worked out once and shared by every line routed over it. */
export function prepareBoxes(boxes) {
  const tiers = new Map();
  return { boxes, tier: (m) => { let s = tiers.get(m); if (!s) { s = sides(boxes, m); tiers.set(m, s); } return s; } };
}

// The card a line end sits on: the box whose `side` edge is nearest the point, among boxes the point faces across.
function ownBox(p, boxes) {
  let best = -1, bestD = Infinity;
  for (let i = 0; i < boxes.length; i++) {
    const b = boxes[i];
    let d;
    if (p.side === 'right' || p.side === 'left') {
      if (p.y < b.y || p.y > b.y + b.h) continue;
      d = Math.abs(p.x - (p.side === 'right' ? b.x + b.w : b.x));
    } else {
      if (p.x < b.x || p.x > b.x + b.w) continue;
      d = Math.abs(p.y - (p.side === 'bottom' ? b.y + b.h : b.y));
    }
    if (d < bestD) { bestD = d; best = i; }
  }
  return bestD <= 12 ? best : -1;
}

// One A* search at one margin and one track. Returns the corner points or null.
function routeOnce(src, dst, tier, boxes, track, placed, pad) {
  const { grown, gap } = tier;
  const f = trackFraction(track);
  let lines = tier.lines.get(track);
  if (!lines) tier.lines.set(track, lines = trackLines(grown, gap, f));
  const inside = (x, y) => { for (const r of grown) if (x > r.x && x < r.x + r.w && y > r.y && y < r.y + r.h) return true; return false; };
  // A stub leaves its card straight out to the card's own track line. It may end short of the handle (which React
  // Flow draws 4 px out) when cards sit that close, but never on or inside its own card.
  const stub = (p) => {
    const [dx, dy] = DIR[p.side] ?? [1, 0];
    const bi = ownBox(p, boxes);
    let x = p.x + dx * (tier.margin + 1), y = p.y + dy * (tier.margin + 1);
    if (bi >= 0) {
      const r = grown[bi], q = gap[bi], b = boxes[bi];
      if (p.side === 'right') x = Math.max(r.x + r.w + f * q.r, b.x + b.w + 1);
      else if (p.side === 'left') x = Math.min(r.x - (1 - f) * q.l, b.x - 1);
      else if (p.side === 'bottom') y = Math.max(r.y + r.h + f * q.b, b.y + b.h + 1);
      else if (p.side === 'top') y = Math.min(r.y - (1 - f) * q.t, b.y - 1);
    }
    return { x, y };
  };
  const s = stub(src), t = stub(dst);
  if (inside(s.x, s.y) || inside(t.x, t.y)) return null;
  // A search window round the two ends (pad px each way), or the whole canvas when pad is Infinity: a line almost
  // never needs to stray far outside the rectangle its ends make, and the window keeps the grid small on a big model.
  const wx0 = Math.min(s.x, t.x) - pad, wx1 = Math.max(s.x, t.x) + pad, wy0 = Math.min(s.y, t.y) - pad, wy1 = Math.max(s.y, t.y) + pad;
  // The stubs themselves must not cross another card. A stub always starts inside its OWN card's grown box, so that
  // one box is excused, and only that one: a neighbour a few px away holds the handle in its grown box too, and
  // excusing every box that holds the handle let a stub run straight through a card dragged 4 to 13 px under another.
  const ownGrown = (p) => { const bi = ownBox(p, boxes); return bi < 0 ? null : grown[bi]; };
  const oS = ownGrown(src), oT = ownGrown(dst);
  for (const r of grown) if (r !== oS && segmentHitsBox(src, s, r)) return null;
  for (const r of grown) if (r !== oT && segmentHitsBox(dst, t, r)) return null;
  const X = windowOf(lines.X, wx0, wx1, s.x, t.x), Y = windowOf(lines.Y, wy0, wy1, s.y, t.y);
  const W = X.length, H = Y.length;
  const si = bsearch(X, s.x), sj = bsearch(Y, s.y), ti = bsearch(X, t.x), tj = bsearch(Y, t.y);
  // Which grid points sit inside a card, and which one-cell steps pass through one, rasterised once per search
  // (the same test as segmentHitsBox: strictly inside; running along a grown border is allowed).
  const cellBad = new Uint8Array(W * H), hBad = new Uint8Array(W * H), vBad = new Uint8Array(W * H);
  for (const r of grown) {
    if (r.x > wx1 || r.x + r.w < wx0 || r.y > wy1 || r.y + r.h < wy0) continue;
    const i0 = firstAbove(X, r.x), i1 = firstAtOrAbove(X, r.x + r.w) - 1;   // X strictly inside (r.x, r.x + r.w)
    const j0 = firstAbove(Y, r.y), j1 = firstAtOrAbove(Y, r.y + r.h) - 1;
    for (let j = j0; j <= j1; j++) {
      const row = j * W;
      for (let i = i0; i <= i1; i++) cellBad[row + i] = 1;
      for (let i = Math.max(0, i0 - 1); i <= Math.min(W - 2, i1); i++) hBad[row + i] = 1;   // step i -> i + 1
    }
    for (let i = i0; i <= i1; i++) for (let j = Math.max(0, j0 - 1); j <= Math.min(H - 2, j1); j++) vBad[j * W + i] = 1;   // step j -> j + 1
  }
  // Below HUG px of margin a grid line can lie on a card's border: the track lines of one card's channel run on across
  // the canvas, and one of them can fall exactly on the edge of a card beside it (review round 4: lines drawn along
  // cards' borders in busy grids). A step along a card's border, within HUG px of it, costs SHARE per px, as a step
  // along another line does, so a line takes it only when nothing else is left. The line's own two cards are excused.
  let hugH = null, hugV = null;
  if (tier.margin < HUG) {
    hugH = new Uint8Array(W * H); hugV = new Uint8Array(W * H);
    const skipA = ownBox(src, boxes), skipB = ownBox(dst, boxes);
    for (let n = 0; n < boxes.length; n++) {
      const b = boxes[n];
      if (n === skipA || n === skipB || b.x > wx1 + HUG || b.x + b.w < wx0 - HUG || b.y > wy1 + HUG || b.y + b.h < wy0 - HUG) continue;
      for (const e of [b.y, b.y + b.h]) for (let j = firstAbove(Y, e - HUG); j < H && Y[j] < e + HUG; j++)
        for (let i = Math.max(0, firstAbove(X, b.x) - 1); i < W - 1 && X[i] < b.x + b.w; i++) if (X[i + 1] > b.x) hugH[j * W + i] = 1;
      for (const e of [b.x, b.x + b.w]) for (let i = firstAbove(X, e - HUG); i < W && X[i] < e + HUG; i++)
        for (let j = Math.max(0, firstAbove(Y, b.y) - 1); j < H - 1 && Y[j] < b.y + b.h; j++) if (Y[j + 1] > b.y) hugV[j * W + i] = 1;
    }
  }
  // Steps that run along a line already placed (within NEAR px, the same way) cost SHARE per px on top of their
  // length, so a line takes another channel or track rather than hide one, and shares only when nothing else is left.
  const hOcc = placed ? new Uint8Array(W * H) : null, vOcc = placed ? new Uint8Array(W * H) : null;
  if (placed) {
    for (let j = 0; j < H; j++) placed.near('h', Y[j], (q) => { for (let i = Math.max(0, firstAbove(X, q.lo) - 1); i < W - 1 && X[i] < q.hi; i++) if (X[i + 1] > q.lo) hOcc[j * W + i] = 1; });
    for (let i = 0; i < W; i++) placed.near('v', X[i], (q) => { for (let j = Math.max(0, firstAbove(Y, q.lo) - 1); j < H - 1 && Y[j] < q.hi; j++) if (Y[j + 1] > q.lo) vOcc[j * W + i] = 1; });
  }
  // State = cell * 4 + the direction it was entered in (0 right, 1 left, 2 down, 3 up).
  const startDir = { right: 0, left: 1, bottom: 2, top: 3 }[src.side] ?? 0;
  const endDir = { right: 1, left: 0, bottom: 3, top: 2 }[dst.side] ?? 1;   // the move that arrives at t heading INTO dst
  const tx = X[ti], ty = Y[tj];
  // Distance left plus the fewest turns still needed, counting the turn into the far stub (0, 1 or 2): never more
  // than the true cost, so A* stays exact, and it keeps the search to a narrow corridor instead of every cell of
  // the rectangle between the two ends.
  const ex = endDir === 0 ? 1 : endDir === 1 ? -1 : 0, ey = endDir === 2 ? 1 : endDir === 3 ? -1 : 0;
  const hOf = (i, j, d) => {
    const dx = tx - X[i], dy = ty - Y[j];
    let turns = 0;
    if (dx || dy) {
      const ax = d === 0 ? 1 : d === 1 ? -1 : 0, ay = d === 2 ? 1 : d === 3 ? -1 : 0;
      const along = dx * ax + dy * ay, across = ax ? dy : dx;
      if (d === endDir) turns = across === 0 && along > 0 ? 0 : 2;          // straight in, or out and back
      else if ((d >> 1) === (endDir >> 1)) turns = 2;                       // heading the opposite way: two
      else turns = (across === 0 && along > 0) || (along >= 0 && dx * ex + dy * ey > 0) ? 1 : 2;   // one turn, or two
    }
    // The tiny share of the distance left breaks ties toward the target, so a plateau of equally short routes is
    // not searched cell by cell (it moves a key by under 0.01 px, far below any real difference in cost).
    return Math.abs(dx) + Math.abs(dy) + turns * BEND;
  };
  const key = (f, hv) => f + hv * 1e-7;
  const N = W * H * 4;
  const g = new Float64Array(N).fill(Infinity);
  const from = new Int32Array(N).fill(-1);
  const heap = new Heap();
  const s0 = (sj * W + si) * 4 + startDir;
  g[s0] = 0; { const hv = hOf(si, sj, startDir); heap.push(key(hv, hv), s0); }
  let goal = -1, steps = 0;
  while (heap.size) {
    const st = heap.pop();
    steps++;
    const d = st & 3, cell = st >> 2, i = cell % W, j = (cell - i) / W;
    if (i === ti && j === tj) { goal = st; break; }
    const gs = g[st];
    for (let m = 0; m < 4; m++) {
      if ((d ^ 1) === m && (d >> 1) === (m >> 1)) continue;   // never double back
      let ni = i, nj = j, sk;
      if (m === 0) { sk = j * W + i; if (i + 1 >= W || hBad[sk]) continue; ni = i + 1; }
      else if (m === 1) { sk = j * W + i - 1; if (i < 1 || hBad[sk]) continue; ni = i - 1; }
      else if (m === 2) { sk = j * W + i; if (j + 1 >= H || vBad[sk]) continue; nj = j + 1; }
      else { sk = (j - 1) * W + i; if (j < 1 || vBad[sk]) continue; nj = j - 1; }
      const nc = nj * W + ni;
      if (cellBad[nc]) continue;
      const len = Math.abs(X[ni] - X[i]) + Math.abs(Y[nj] - Y[j]);
      let cost = gs + len + (m === d ? 0 : BEND);
      if (placed && (m < 2 ? hOcc[sk] : vOcc[sk])) cost += len * SHARE;
      if (hugH && (m < 2 ? hugH[sk] : hugV[sk])) cost += len * SHARE;
      if (nc === tj * W + ti && m !== endDir) cost += BEND;   // arriving sideways means one more turn at the stub
      const ns = nc * 4 + m;
      if (cost < g[ns]) { g[ns] = cost; from[ns] = st; const hv = hOf(ni, nj, m); heap.push(key(cost + hv, hv), ns); }
    }
  }
  work.steps += steps;
  if (goal < 0) return null;
  const cells = [];
  for (let st = goal; st >= 0; st = from[st]) { const c = st >> 2; cells.push({ x: X[c % W], y: Y[Math.floor(c / W)] }); }
  cells.reverse();
  return simplify([{ x: src.x, y: src.y }, ...cells, { x: dst.x, y: dst.y }]);
}

/**
 * @param src    the line's start on its card's border, with the side it leaves by: { x, y, side }
 * @param dst    the line's end, the same shape
 * @param boxes  every card's box in flow coordinates: [{ x, y, w, h }], the two joined cards included (or the
 *               result of prepareBoxes, to share the work across many lines)
 * @param track  which parallel track (0, 1, 2...) this line takes inside every channel it uses
 * @returns      the corner points from src to dst, or null when no route clears every card
 */
export function routeOrthogonal(src, dst, boxes, track = 0) {
  return drain(routeWith(src, dst, Array.isArray(boxes) ? prepareBoxes(boxes) : boxes, track))?.pts ?? null;
}

// The first margin that has a route wins, unless `every` is set: then a route that runs along a placed line does not
// end the search, and the least-shared route across the margins wins (a smaller margin may leave a slit wide enough
// for one more track). A generator that yields after each search, so a long pass can be spread over frames (routeSteps).
function* routeWith(src, dst, prep, track, placed, every = false) {
  let best = null, bestShared = Infinity;
  for (const m of MARGINS) for (const pad of [WINDOW, Infinity]) {
    const pts = routeOnce(src, dst, prep.tier(m), prep.boxes, track, placed, pad);
    yield;
    if (!pts) continue;
    if (!every) return { pts, margin: m };
    const shared = sumOf(placed.shared(pts));
    if (!shared) return { pts, margin: m };
    if (shared < bestShared) { bestShared = shared; best = { pts, margin: m }; }
    break;
  }
  if (best) return best;
  // Last resort: a card lies over the line's own handle (cards stacked on each other), so no route can reach it
  // without passing under that card. Only the part of that card between the handle and the handle's own card is let
  // through (the card is cut at the handle), so the line leaves from under it by the shortest way and routes round
  // everything else. Dropping the whole card let a line run under all of it, 190 px for a card saved 4 px away.
  let cut = null;
  for (const p of [src, dst]) prep.boxes.forEach((b, i) => {
    if (!covers(b, p)) return;
    const c = (cut ??= prep.boxes.slice())[i];
    if (p.side === 'right') cut[i] = { ...c, x: p.x, w: c.x + c.w - p.x };
    else if (p.side === 'left') cut[i] = { ...c, w: p.x - c.x };
    else if (p.side === 'bottom') cut[i] = { ...c, y: p.y, h: c.y + c.h - p.y };
    else cut[i] = { ...c, h: p.y - c.y };
  });
  if (cut) { const open = prepareBoxes(cut); const pts = routeOnce(src, dst, open.tier(0), cut, track, placed, Infinity); if (pts) return { pts, margin: -1 }; }
  return null;
}

const sumOf = (shared) => { let t = 0; for (const run of shared.values()) t += run; return t; };
// Straight runs of a route, split by direction, for the "drawn on top of each other" test.
function runsOf(pts) {
  const v = [], h = [];
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i];
    if (a.x === b.x && a.y !== b.y) v.push({ at: a.x, lo: Math.min(a.y, b.y), hi: Math.max(a.y, b.y) });
    else if (a.y === b.y && a.x !== b.x) h.push({ at: a.y, lo: Math.min(a.x, b.x), hi: Math.max(a.x, b.x) });
  }
  return { v, h };
}
/** How many px two routes run along the same line (within NEAR px of each other, the same way). */
export function sharedLength(A, B) {
  const a = runsOf(A), b = runsOf(B);
  let run = 0;
  for (const k of ['v', 'h']) for (const p of a[k]) for (const q of b[k]) {
    if (Math.abs(p.at - q.at) >= NEAR) continue;
    run += Math.max(0, Math.min(p.hi, q.hi) - Math.max(p.lo, q.lo));
  }
  return run;
}

// Placed runs, bucketed by their rounded coordinate so a new route is checked only against runs near it.
class RunIndex {
  constructor() { this.v = new Map(); this.h = new Map(); }
  add(id, pts) {
    const r = runsOf(pts);
    for (const k of ['v', 'h']) for (const s of r[k]) { const key = Math.round(s.at); let a = this[k].get(key); if (!a) this[k].set(key, a = []); a.push({ ...s, id }); }
  }
  remove(id, pts) {
    const r = runsOf(pts);
    for (const k of ['v', 'h']) for (const s of r[k]) { const key = Math.round(s.at), a = this[k].get(key); if (a) this[k].set(key, a.filter((q) => q.id !== id)); }
  }
  // Calls fn with every placed run of kind k ('v' or 'h') within NEAR px of the coordinate `at`.
  near(k, at, fn) {
    const c = Math.round(at);
    for (let key = c - NEAR; key <= c + NEAR; key++) for (const q of this[k].get(key) ?? []) if (Math.abs(q.at - at) < NEAR) fn(q);
  }
  // The ids of placed lines this route runs along, with the shared length for each.
  shared(pts) {
    const r = runsOf(pts), out = new Map();
    for (const k of ['v', 'h']) for (const s of r[k]) {
      const c = Math.round(s.at);
      for (let key = c - NEAR; key <= c + NEAR; key++) for (const q of this[k].get(key) ?? []) {
        if (Math.abs(q.at - s.at) >= NEAR) continue;
        const run = Math.min(q.hi, s.hi) - Math.max(q.lo, s.lo);
        if (run > 0) out.set(q.id, (out.get(q.id) ?? 0) + run);
      }
    }
    for (const [id, run] of out) if (run <= RUN) out.delete(id);
    return out;
  }
}

// True while a kept route still clears every card at the margin it was routed with (each stub may cross its own
// card's grown box, and only its own: the same rule as routeOnce).
function stillClear({ pts, margin, src, dst }, prep) {
  if (margin < 0) return false;
  const { grown } = prep.tier(margin);
  const a0 = ownBox(src, prep.boxes), z0 = ownBox(dst, prep.boxes);
  for (let i = 1; i < pts.length; i++) for (let j = 0; j < grown.length; j++) {
    if (!segmentHitsBox(pts[i - 1], pts[i], grown[j])) continue;
    if ((i === 1 && j === a0) || (i === pts.length - 1 && j === z0)) continue;
    return false;
  }
  return true;
}
const sameEnd = (a, b) => a && b && a.side === b.side && Math.abs(a.x - b.x) < 0.01 && Math.abs(a.y - b.y) < 0.01;

/**
 * Route every line at once, so each takes its own track: the lowest track whose route does not run along a line
 * already placed (checked for real, not assumed from the track number).
 * @param lines  [{ id, src, dst }] with src and dst as routeOrthogonal takes them
 * @param boxes  every card's box
 * @param prev   (optional) the previous result, whose routes are KEPT where a line's two ends did not move and its
 *               route still clears every card: a drag re-routes only the lines it disturbs. A drag frame also skips
 *               routing sharing lines again (the spread still runs); the drop's full pass does it.
 * @returns      Map id -> { pts, track, margin, src, dst } (pts null when no route clears the cards)
 */
export function routeAll(lines, boxes, prev) {
  return drain(routeSteps(lines, boxes, prev));
}

/**
 * routeAll as a generator: the same pass, yielding after every search and every line the spread looks at, so the
 * Diagram can run a big model's pass a few ms at a time and the page keeps answering (review round 5: one pass of a
 * 300-table grid took 30 s on the main thread). Run it to the end (drain) for the result; stop calling next() to drop it.
 */
export function* routeSteps(lines, boxes, prev) {
  const prep = prepareBoxes(boxes);
  const out = new Map(), index = new RunIndex(), todo = [];
  for (const l of lines) {
    const p = prev?.get(l.id);
    if (p?.pts && sameEnd(p.src, l.src) && sameEnd(p.dst, l.dst) && stillClear(p, prep)) { out.set(l.id, p); index.add(l.id, p.pts); }
    else todo.push(l);
  }
  // The lowest track whose route runs along no placed line; failing that, the least-shared route of the tries.
  const place = function* (l, every) {
    const tried = new Set(), taken = new Set();
    let best = null, bestShared = Infinity;
    for (let n = 0; n < TRIES; n++) {
      let t = 0; while (tried.has(t) || taken.has(t)) t++;
      tried.add(t);
      const r = yield* routeWith(l.src, l.dst, prep, t, index, every);
      if (!r) { if (n === 0) break; continue; }
      const shared = index.shared(r.pts);
      let total = 0; for (const [id, run] of shared) { total += run; taken.add(out.get(id).track); }
      if (total < bestShared) { bestShared = total; best = { ...r, track: t, shared: total }; }
      if (!total) break;
    }
    return best;
  };
  const start = work.steps;
  for (const l of todo) {
    const best = yield* place(l, false);
    const rec = { pts: best?.pts ?? null, track: best?.track ?? 0, margin: best?.margin ?? MARGIN, src: l.src, dst: l.dst };
    out.set(l.id, rec);
    if (rec.pts) index.add(l.id, rec.pts);
  }
  // A drag frame spreads only the lines it routed again: the kept ones were spread by an earlier pass, and spreading
  // every line on every frame made a long drag at 300 tables stall half again as long as before spreading (round 4).
  yield* spread(out, prep, prev ? new Set(todo.map((l) => l.id)) : null);
  if (prev) return out;
  // A line the spread could not free is routed again against every other line where they now lie, trying every
  // margin, and kept only when it then shares less. Bounded, so a layout packed past any fix costs a little, not a freeze.
  const again = new RunIndex();
  for (const [id, r] of out) if (r.pts) again.add(id, r.pts);
  index.v = again.v; index.h = again.h;
  const from = work.steps, budget = Math.max(REROUTE_MIN, (from - start) * REROUTE);
  let moved = false;
  for (const l of lines) {
    if (work.steps - from > budget) break;
    const cur = out.get(l.id);
    if (!cur.pts || cur.margin < 0) continue;
    index.remove(l.id, cur.pts);
    const now = sumOf(index.shared(cur.pts));
    const best = now ? yield* place(l, true) : null;
    if (best && best.shared < now) { out.set(l.id, { pts: best.pts, track: best.track, margin: best.margin, src: l.src, dst: l.dst }); index.add(l.id, best.pts); moved = true; }
    else index.add(l.id, cur.pts);
  }
  if (moved) yield* spread(out, prep);
  return out;
}

/**
 * The runs still drawn too close to something once a pass has landed: along another line (within NEAR px for over
 * RUN px) or along the border of a card the line does not join (within HUG px for over RUN px). A measure of a pass,
 * for the tests: the page no longer reads it (review round 5), because making room from drawn routes meant routing
 * again and again; it makes room before it routes (diagramfit.mjs roomSteps).
 * @param routes  routeAll's result
 * @param boxes   every card's box, as routed
 * @returns       [{ id, k: 'v' | 'h', at, lo, hi }], one per crowded run
 */
export function crowdedRuns(routes, boxes) {
  const idx = new RunIndex(), all = [];
  for (const [id, r] of routes) {
    if (!r.pts) continue;
    idx.add(id, r.pts);
    const own = new Set([ownBox(r.src, boxes), ownBox(r.dst, boxes)]);
    const { v, h } = runsOf(r.pts);
    for (const s of v) all.push({ id, k: 'v', ...s, own });
    for (const s of h) all.push({ id, k: 'h', ...s, own });
  }
  const out = [];
  for (const s of all) {
    let crowded = false;
    idx.near(s.k, s.at, (q) => { if (!crowded && q.id !== s.id && Math.min(q.hi, s.hi) - Math.max(q.lo, s.lo) > RUN) crowded = true; });
    for (let i = 0; i < boxes.length && !crowded; i++) {
      if (s.own.has(i)) continue;
      const b = boxes[i], e0 = s.k === 'v' ? b.x : b.y, e1 = e0 + (s.k === 'v' ? b.w : b.h);
      const t0 = s.k === 'v' ? b.y : b.x, t1 = t0 + (s.k === 'v' ? b.h : b.w);
      if ((Math.abs(s.at - e0) < HUG || Math.abs(s.at - e1) < HUG) && Math.min(t1, s.hi) - Math.max(t0, s.lo) > RUN) crowded = true;
    }
    if (crowded) out.push({ id: s.id, k: s.k, at: s.at, lo: s.lo, hi: s.hi });
  }
  return out;
}

/**
 * What to draw until the next pass lands, when cards moved and no pass has routed the lines yet: a burst of re-flow on
 * a big model (Expand all) takes several passes, and leaving the moved lines out meant every line vanished for a
 * second or more. A line whose ends did not move keeps its route. A line whose ends moved keeps its old corners, with
 * the first and last moved just enough that it leaves both handles along their sides again. That held route is marked
 * `held` (drawn faded, never kept by a drag, margin -1) because the cards have moved under it: until the pass lands it
 * can cross a card that just grew. A line never routed yet has nothing to hold and waits for the pass.
 * @param prev   the last routes drawn
 * @param lines  [{ id, src, dst }] where the lines' ends are now
 * @returns      Map id -> { pts, track, margin, src, dst, held? }
 */
export function holdRoutes(prev, lines) {
  const out = new Map();
  if (!prev) return out;
  for (const l of lines) {
    const p = prev.get(l.id);
    if (!p) continue;
    if (sameEnd(p.src, l.src) && sameEnd(p.dst, l.dst)) out.set(l.id, p);
    else out.set(l.id, { pts: p.pts ? reattach(p.pts, l.src, l.dst) : null, track: p.track, margin: -1, src: l.src, dst: l.dst, held: true });
  }
  return out;
}
const across = (e) => e.side === 'left' || e.side === 'right';
// An old route moved onto new ends: every inner corner stays, the first and last corners slide so the end runs keep
// leaving along their handles' sides. A route of one or two runs is drawn afresh as the plain L or Z between the ends.
function reattach(pts, s, t) {
  const hs = across(s), ht = across(t);
  if (pts.length >= 4) {
    const q = pts.map((p) => ({ x: p.x, y: p.y }));
    const e = q.length - 2;
    q[0] = { x: s.x, y: s.y }; q[e + 1] = { x: t.x, y: t.y };
    if (hs) q[1].y = s.y; else q[1].x = s.x;
    if (ht) q[e].y = t.y; else q[e].x = t.x;
    return simplify(q);
  }
  if (hs !== ht) return simplify([{ x: s.x, y: s.y }, hs ? { x: t.x, y: s.y } : { x: s.x, y: t.y }, { x: t.x, y: t.y }]);
  const mx = (s.x + t.x) / 2, my = (s.y + t.y) / 2;
  return simplify(hs ? [{ x: s.x, y: s.y }, { x: mx, y: s.y }, { x: mx, y: t.y }, { x: t.x, y: t.y }]
    : [{ x: s.x, y: s.y }, { x: s.x, y: my }, { x: t.x, y: my }, { x: t.x, y: t.y }]);
}

// Lines still drawn along each other once every line is placed (a channel busier than its tracks allow, or two
// channels' tracks that fall within NEAR px of each other) are pulled apart here: one middle run of the line slides
// sideways inside its own channel to the nearest place where the line runs along less of the others. A stub on a
// handle never moves. The move keeps both neighbouring runs heading the same way (a stub keeps room for its end
// marker), and the run and both neighbours must still clear every card, at the line's own margin where they can and
// a smaller one where they must. Running along a line within TIGHT px weighs far more than within NEAR, so where a
// channel has no room for NEAR the lines still end TIGHT px apart. Every move shrinks that weighted total, so it ends.
function* spread(out, prep, only = null) {
  const ids = [];
  for (const [id, r] of out) if (r.pts && r.margin >= 0 && r.pts.length > 3 && (!only || only.has(id))) ids.push(id);
  if (!ids.length) return;
  const idx = { v: new Map(), h: new Map() }, segs = new Map();
  const segOf = (id, pts, i) => {
    const a = pts[i - 1], b = pts[i];
    if (a.x === b.x && a.y !== b.y) return { id, k: 'v', at: a.x, lo: Math.min(a.y, b.y), hi: Math.max(a.y, b.y) };
    if (a.y === b.y && a.x !== b.x) return { id, k: 'h', at: a.y, lo: Math.min(a.x, b.x), hi: Math.max(a.x, b.x) };
    return null;
  };
  const put = (s) => { const key = Math.round(s.at); let a = idx[s.k].get(key); if (!a) idx[s.k].set(key, a = []); a.push(s); };
  const take = (s) => { const a = idx[s.k].get(Math.round(s.at)); const j = a ? a.indexOf(s) : -1; if (j >= 0) a.splice(j, 1); };
  for (const [id, r] of out) if (r.pts) { const list = []; for (let i = 1; i < r.pts.length; i++) { const s = segOf(id, r.pts, i); list.push(s); if (s) put(s); } segs.set(id, list); }
  // Adds to acc, for each other line the run s of line `id` runs along, [px within NEAR, px within TIGHT].
  const shareOf = (id, s, acc) => {
    const c = Math.round(s.at);
    for (let key = c - NEAR; key <= c + NEAR; key++) for (const q of idx[s.k].get(key) ?? []) {
      const d = Math.abs(q.at - s.at);
      if (q.id === id || d >= NEAR) continue;
      const run = Math.min(q.hi, s.hi) - Math.max(q.lo, s.lo);
      if (run > 0) { let e = acc.get(q.id); if (!e) acc.set(q.id, e = [0, 0]); e[0] += run; if (d < TIGHT) e[1] += run; }
    }
  };
  const worth = (near, tight) => (near > RUN ? near : 0) + (tight > RUN ? 1000 * tight : 0);
  const total = (acc) => { let t = 0; for (const [a, b] of acc.values()) t += worth(a, b); return t; };
  const runOf = (u, w) => (u.x === w.x ? { k: 'v', at: u.x, lo: Math.min(u.y, w.y), hi: Math.max(u.y, w.y) } : { k: 'h', at: u.y, lo: Math.min(u.x, w.x), hi: Math.max(u.x, w.x) });
  // A line that found no move is tried again only once a line it runs along has moved.
  let movedBefore = null;
  for (let round = 0; round < SPREAD_ROUNDS; round++) {
    const movedNow = new Set();
    for (const id of ids) {
      yield;
      let rec = out.get(id);
      const list = segs.get(id);
      const acc0 = new Map();
      for (const s of list) if (s) shareOf(id, s, acc0);
      let now = total(acc0);
      if (!now || (movedBefore && !movedBefore.has(id) && ![...acc0.keys()].some((k) => movedBefore.has(k)))) continue;
      const n = rec.pts.length;
      const oS = ownBox(rec.src, prep.boxes), oT = ownBox(rec.dst, prep.boxes);
      for (let i = 2; i <= n - 2 && now; i++) {
        const s = list[i - 1];
        if (!s) continue;
        const pts = rec.pts, p = s.k === 'v' ? 'x' : 'y', q = s.k === 'v' ? 'y' : 'x';
        const A = pts[i - 2], B = pts[i + 1], c = s.at;   // the run is pts[i-1]..pts[i]; its neighbours end at A and B
        let lo = c - SPREAD_REACH, hi = c + SPREAD_REACH;
        const room = (o, isStub) => { const d = isStub ? Math.min(8, Math.abs(c - o)) : 1; if (o < c) lo = Math.max(lo, o + d); else hi = Math.min(hi, o - d); };
        room(A[p], i === 2); room(B[p], i === n - 2);
        if (!(lo < hi)) continue;
        // The places each margin leaves open: the run's own channel, and how far each neighbour may reach.
        const ivs = [];
        for (const m of MARGINS) {
          if (m > rec.margin) continue;
          const { grown } = prep.tier(m);
          const exA = i === 2 && oS >= 0 ? grown[oS] : null, exB = i === n - 2 && oT >= 0 ? grown[oT] : null;
          let vl = lo, vh = hi, ok = true;
          for (const r of grown) {
            const r0 = p === 'x' ? r.x : r.y, r1 = r0 + (p === 'x' ? r.w : r.h), t0 = p === 'x' ? r.y : r.x, t1 = t0 + (p === 'x' ? r.h : r.w);
            if (t0 < s.hi && t1 > s.lo) { if (r0 < c && r1 > c) { ok = false; break; } if (r1 <= c) vl = Math.max(vl, r1); else vh = Math.min(vh, r0); }
            for (let e = 0; e < 2 && ok; e++) {
              const E = e ? B : A;
              if (r === (e ? exB : exA) || !(E[q] > t0 && E[q] < t1)) continue;   // this box does not lie across the neighbour
              const at = E[p];   // where the neighbour ends: a handle within EPS of a card's border is on it, not inside
              if (c > at) { if (r0 <= at - EPS && r1 > at + EPS) ok = false; else if (r0 >= at - EPS) vh = Math.min(vh, r0); }
              else { if (r1 >= at + EPS && r0 < at - EPS) ok = false; else if (r1 <= at + EPS) vl = Math.max(vl, r1); }
            }
            if (!ok) break;
          }
          if (ok && vl < vh) ivs.push([m, vl, vh]);
        }
        if (!ivs.length) continue;
        // Where the run could go: the edges of those places, and just clear of each parallel run near it (what the
        // run itself shares changes only there).
        const cand = new Set();
        for (const [, vl, vh] of ivs) { cand.add(vl); cand.add(vh); }
        for (let key = Math.floor(lo) - NEAR - 1; key <= Math.ceil(hi) + NEAR + 1; key++) for (const o of idx[s.k].get(key) ?? []) {
          if (o.id === id || !(o.hi > s.lo && o.lo < s.hi)) continue;
          for (const d of [NEAR, TIGHT]) { cand.add(o.at - d - 0.5); cand.add(o.at + d + 0.5); }
        }
        // What the rest of the line shares is the same wherever the run goes.
        const base = new Map();
        for (let j = 0; j < list.length; j++) if (list[j] && (j < i - 2 || j > i)) shareOf(id, list[j], base);
        const baseTotal = total(base), top = ivs[0][0];
        // A run drawn along a card's border (within HUG px of it) cannot be told from the border (review round 4: sliding
        // runs onto a channel's edges drew lines along cards' borders). Each px of it weighs HUG_WEIGHT: far more than a px
        // within NEAR of another line, less than a px within TIGHT, so the spread puts a run on a border only to part two
        // lines drawn on top of each other, as in a slit a few px wide that the page has not been let make room in.
        const along = prep.boxes.filter((r) => (p === 'x' ? r.y < s.hi && r.y + r.h > s.lo : r.x < s.hi && r.x + r.w > s.lo));
        const hugOf = (v) => {
          let t = 0;
          for (const r of along) {
            const r0 = p === 'x' ? r.x : r.y, r1 = r0 + (p === 'x' ? r.w : r.h);
            if (Math.abs(v - r0) < HUG || Math.abs(v - r1) < HUG) t += Math.min(s.hi, p === 'x' ? r.y + r.h : r.x + r.w) - Math.max(s.lo, p === 'x' ? r.y : r.x);
          }
          return t > RUN ? HUG_WEIGHT * t : 0;
        };
        const worstNow = now + hugOf(c);
        let pick = null;
        for (const v of [...cand].sort((x, y) => Math.abs(x - c) - Math.abs(y - c))) {
          if (v === c) continue;
          let m = -1;
          for (const [mm, vl, vh] of ivs) if (v >= vl && v <= vh) { m = mm; break; }
          if (m < 0) continue;
          const add = new Map();
          const a = { ...pts[i - 1], [p]: v }, b = { ...pts[i], [p]: v };
          for (const z of [runOf(A, a), runOf(a, b), runOf(b, B)]) if (z.hi > z.lo) shareOf(id, z, add);
          let sh = baseTotal;
          for (const [k, e] of add) { const o = base.get(k); sh += worth((o?.[0] ?? 0) + e[0], (o?.[1] ?? 0) + e[1]) - (o ? worth(o[0], o[1]) : 0); }
          const bad = sh + hugOf(v);
          if (bad >= worstNow) continue;
          if (!pick || bad < pick.bad || (bad === pick.bad && m > pick.m)) pick = { v, sh, bad, m };
          if (pick.bad === 0 && pick.m === top) break;   // the nearest clean place at the best margin: nothing beats it
        }
        if (!pick) continue;
        for (const j of [i - 1, i, i + 1]) if (list[j - 1]) take(list[j - 1]);
        const np = pts.map((o) => ({ ...o }));
        np[i - 1][p] = pick.v; np[i][p] = pick.v;
        rec = { ...rec, pts: np, margin: Math.min(rec.margin, pick.m) };
        out.set(id, rec);
        for (const j of [i - 1, i, i + 1]) { const z = segOf(id, np, j); list[j - 1] = z; if (z) put(z); }
        now = pick.sh; movedNow.add(id);
      }
    }
    if (!movedNow.size) break;
    movedBefore = movedNow;
  }
}

/** Runs a generator (routeSteps, demandSteps, diagramfit's roomSteps) to its end and returns what it returns. */
export function drain(g) {
  for (;;) { const r = g.next(); if (r.done) return r.value; }
}

/**
 * Which way each line goes, cheaply: every line routed once, on the middle track, at the first margin that has a route,
 * either on its own (its shortest way) or `shared`: against the lines routed before it, a run along one costing what it
 * costs in a full pass, so the lines share out side-by-side gaps the way a full pass does. It is the demand the channels
 * between cards must carry, for making room (diagramfit.mjs roomSteps counts the lines through each gap from both),
 * found without the tracks, the spread or routing again, which are what make a full pass cost seconds on a big model.
 * A generator that yields after each search.
 * @returns  Map id -> { pts, track, margin, src, dst } (pts null when no route clears the cards)
 */
export function* demandSteps(lines, boxes, shared = false) {
  const prep = prepareBoxes(boxes), out = new Map(), index = shared ? new RunIndex() : null;
  for (const l of lines) {
    const r = yield* routeWith(l.src, l.dst, prep, 0, index, false);
    out.set(l.id, { pts: r?.pts ?? null, track: 0, margin: r?.margin ?? MARGIN, src: l.src, dst: l.dst });
    if (r && index) index.add(l.id, r.pts);
  }
  return out;
}

// Sorted distinct values, and the index helpers over them.
function uniq(a) { a.sort((p, q) => p - q); const o = []; for (const v of a) if (!o.length || v !== o[o.length - 1]) o.push(v); return o; }
function bsearch(a, v) { let lo = 0, hi = a.length - 1; while (lo <= hi) { const m = (lo + hi) >> 1; if (a[m] === v) return m; if (a[m] < v) lo = m + 1; else hi = m - 1; } return -1; }
function firstAbove(a, v) { let lo = 0, hi = a.length; while (lo < hi) { const m = (lo + hi) >> 1; if (a[m] > v) hi = m; else lo = m + 1; } return lo; }
function firstAtOrAbove(a, v) { let lo = 0, hi = a.length; while (lo < hi) { const m = (lo + hi) >> 1; if (a[m] >= v) hi = m; else lo = m + 1; } return lo; }

// A binary min-heap of (key, state) in typed arrays, so a search allocates no object per step.
class Heap {
  constructor() { this.k = new Float64Array(1024); this.v = new Int32Array(1024); this.size = 0; }
  push(key, val) {
    if (this.size === this.k.length) { const k = new Float64Array(this.size * 2); k.set(this.k); this.k = k; const v = new Int32Array(this.size * 2); v.set(this.v); this.v = v; }
    let i = this.size++;
    const K = this.k, V = this.v;
    while (i > 0) { const p = (i - 1) >> 1; if (K[p] <= key) break; K[i] = K[p]; V[i] = V[p]; i = p; }
    K[i] = key; V[i] = val;
  }
  pop() {
    const K = this.k, V = this.v, top = V[0], n = --this.size;
    if (n > 0) {
      const key = K[n], val = V[n];
      let i = 0;
      for (;;) { const l = 2 * i + 1; if (l >= n) break; const r = l + 1; const c = r < n && K[r] < K[l] ? r : l; if (K[c] >= key) break; K[i] = K[c]; V[i] = V[c]; i = c; }
      K[i] = key; V[i] = val;
    }
    return top;
  }
}

// Drop repeated points and the middle point of any three in a straight line, so a path lists only its corners.
function simplify(pts) {
  const out = [];
  for (const p of pts) {
    if (out.length && out[out.length - 1].x === p.x && out[out.length - 1].y === p.y) continue;
    if (out.length >= 2) {
      const a = out[out.length - 2], b = out[out.length - 1];
      if ((a.x === b.x && b.x === p.x) || (a.y === b.y && b.y === p.y)) { out[out.length - 1] = p; continue; }
    }
    out.push(p);
  }
  return out;
}

/** An SVG path through the points, straight segments only. */
export const pathOf = (pts) => pts.map((p, i) => `${i ? 'L' : 'M'}${p.x},${p.y}`).join(' ');

/**
 * The bus matrix's order (Kane, 8 Oct): which tables are facts, which are lookup tables, and the order each is laid in.
 * A lookup table a fact uses sits in the one row across the top; one that only other lookup tables use (an outrigger,
 * such as Product Category under Product Subcategory) sits right after the lookup table it hangs off, so a snowflake
 * reads as a chain in the row instead of landing among the facts. The row and the fact column are ordered to cross as
 * few lines as they can: a line drops from its lookup table and turns left into its fact, so it crosses another line
 * exactly when it comes from further right and reaches a higher fact.
 * @param names  every table, in model order
 * @param links  [{ many, one }], one per relationship; a link naming a table not in `names` is skipped
 * @returns      { dims: row order, left to right; facts: top to bottom; iso: unlinked tables, model order;
 *                 parent: { outrigger: the lookup table it sits beside } }
 */
export function busOrder(names, links) {
  const has = new Set(names), pos = new Map(names.map((n, i) => [n, i]));
  const ls = links.filter((l) => has.has(l.many) && has.has(l.one) && l.many !== l.one);
  const isOne = new Set(ls.map((l) => l.one)), isMany = new Set(ls.map((l) => l.many));
  const dims = names.filter((n) => isOne.has(n)), facts = names.filter((n) => !isOne.has(n) && isMany.has(n));
  const iso = names.filter((n) => !isOne.has(n) && !isMany.has(n));
  const factSet = new Set(facts);
  // Lines between a fact and a lookup table, counted per pair (Date is often used three times by one fact).
  const uses = new Map();   // dim -> Map(fact -> lines)
  for (const l of ls) if (factSet.has(l.many)) {
    let m = uses.get(l.one); if (!m) uses.set(l.one, m = new Map());
    m.set(l.many, (m.get(l.many) ?? 0) + 1);
  }
  const primary = dims.filter((d) => uses.has(d));
  const linksOf = (d) => [...(uses.get(d)?.values() ?? [])].reduce((a, b) => a + b, 0);
  // Start: facts with the most lines on top, then the row by the mean height of the facts each table reaches.
  let factOrder = [...facts].sort((a, b) => countFact(b) - countFact(a) || pos.get(a) - pos.get(b));
  function countFact(f) { let c = 0; for (const m of uses.values()) c += m.get(f) ?? 0; return c; }
  const factIx = () => new Map(factOrder.map((f, i) => [f, i]));
  let fi = factIx();
  const bary = (d) => { let s = 0, c = 0; for (const [f, k] of uses.get(d)) { s += fi.get(f) * k; c += k; } return s / c; };
  let row = [...primary].sort((a, b) => bary(a) - bary(b) || linksOf(b) - linksOf(a) || pos.get(a) - pos.get(b));
  // Then a few rounds of moving each table to the place in its line that crosses least, the row and the column in turn.
  const rowCost = () => pairCost(row, (a, b) => {   // a left of b: b's line to a higher fact crosses a's line to a lower one
    let c = 0; for (const [fa, ka] of uses.get(a)) for (const [fb, kb] of uses.get(b)) if (fi.get(fb) < fi.get(fa)) c += ka * kb; return c;
  });
  const colCost = () => {   // f above g: g's line from a table further left crosses f's line from one further right
    const di = new Map(row.map((d, i) => [d, i]));
    const by = new Map(facts.map((f) => [f, []]));
    for (const d of row) for (const [f, k] of uses.get(d)) by.get(f).push([di.get(d), k]);
    return pairCost(factOrder, (f, g) => { let c = 0; for (const [x, k] of by.get(f)) for (const [y, j] of by.get(g)) if (y < x) c += k * j; return c; });
  };
  for (let round = 0; round < 4; round++) {
    const r = sift(row, rowCost()), f = sift(factOrder, colCost());
    row = r.order; factOrder = f.order; fi = factIx();
    if (!r.moved && !f.moved) break;
  }
  // Outriggers: each one beside the first lookup table that uses it, reached from the row in order (nearest first).
  const parent = {}, kids = new Map();
  const queue = [...row], placed = new Set(row);
  while (queue.length) {
    const d = queue.shift();
    for (const l of ls) if (l.many === d && isOne.has(l.one) && !placed.has(l.one)) {
      placed.add(l.one); parent[l.one] = d; queue.push(l.one);
      let k = kids.get(d); if (!k) kids.set(d, k = []); k.push(l.one);
    }
  }
  const out = [];
  const emit = (d) => { out.push(d); for (const k of kids.get(d) ?? []) emit(k); };
  row.forEach(emit);
  dims.filter((d) => !placed.has(d)).forEach((d) => { if (!out.includes(d)) out.push(d); });   // a ring of lookup tables no fact reaches
  return { dims: out, facts: factOrder, iso, parent };
}

// Pairwise crossing costs for an order: cost(a, b) is what a placed before b costs.
function pairCost(order, cost) {
  const n = order.length, C = Array.from({ length: n }, () => new Float64Array(n));
  for (let i = 0; i < n; i++) for (let j = 0; j < n; j++) if (i !== j) C[i][j] = cost(order[i], order[j]);
  return C;
}
// Sifting: take each item out and put it back where the order costs least; a tie keeps it where it was.
function sift(order, C) {
  let ids = order.map((_, i) => i), moved = false;
  for (let x = 0; x < order.length; x++) {
    const was = ids.indexOf(x), rest = ids.filter((y) => y !== x);
    let cur = 0;   // x first: before every other item
    for (const y of rest) cur += C[x][y];
    let best = cur, bestAt = 0, atWas = cur;
    for (let p = 0; p < rest.length; p++) {
      cur += C[rest[p]][x] - C[x][rest[p]];   // x moves to after rest[p]
      if (p + 1 === was) atWas = cur;
      if (cur < best - 1e-9) { best = cur; bestAt = p + 1; }
    }
    if (bestAt !== was && best < atWas - 1e-9) { rest.splice(bestAt, 0, x); ids = rest; moved = true; }
  }
  return { order: ids.map((i) => order[i]), moved };
}

/**
 * The bus matrix's whole placement (Kane, 8 Oct): the lookup tables in ONE row across the top, the facts in a column
 * down the left starting under that row, and unlinked tables after the row on its right (rows above it when many). The row never wraps: a model wider than the screen scrolls sideways, and the first frame shows the
 * facts and the first lookup tables at a readable zoom (readableFirstView). The 27 Sep space rule wrapped the row to
 * the canvas's shape so Fit drew a big model larger, but its rows of lookup tables put lines behind cards and among
 * the facts. Order the tables with busOrder first.
 * @param dims    [{ w, h }] in row order; facts, iso the same
 * @param aspect  unused since the row stopped wrapping; kept so every caller keeps one signature
 * @returns       { dims: [{x, y}], facts: [{x, y}], iso: [{x, y}], cols: the row's length, isoCols: unlinked per row }
 */
export function busGrid(dims, facts, iso, aspect, o = {}) {
  const { nodeW = 190, nodeH = 92, gap = 72, colGap = 96, rowGap = 110, isoGap = 60 } = o;
  const factColW = Math.max(nodeW, ...facts.map((b) => b.w));
  const rowH = Math.max(nodeH, ...dims.map((b) => b.h));
  const out = { dims: [], facts: [], iso: [], cols: Math.max(1, dims.length), isoCols: 1 };
  let x = factColW + colGap;
  for (const b of dims) { out.dims.push({ x, y: 0 }); x += Math.max(nodeW, b.w) + colGap; }
  let y = rowH + rowGap;
  for (const b of facts) { out.facts.push({ x: 0, y }); y += b.h + gap; }
  if (iso.length) {
    // Unlinked tables carry on the row to the right, and a long run goes up in rows above it, never down beside the
    // facts: a card level with a fact makes the router's channel off that fact's right side as wide as the whole row,
    // and its lines then left the fact halfway across the diagram and came back (diagramroute's stub).
    const k = iso.length <= 4 ? iso.length : Math.ceil(Math.sqrt(iso.length * 2));
    out.isoCols = k;
    const colW = Array.from({ length: k }, (_, c) => Math.max(nodeW, ...iso.filter((_, i) => i % k === c).map((b) => b.w)));
    const rows = Math.ceil(iso.length / k);
    let bottom = rowH;   // each row of them ends where the one under it starts (the first: where the row ends)
    for (let r = 0; r < rows; r++) {
      const cardsInRow = iso.slice(r * k, r * k + k), top = bottom - Math.max(...cardsInRow.map((b) => b.h));
      let cx = (dims.length ? x - colGap : factColW) + isoGap;
      cardsInRow.forEach((b, c) => { out.iso.push({ x: cx, y: top }); cx += colW[c] + gap; });
      bottom = top - gap;
    }
  }
  return out;
}
