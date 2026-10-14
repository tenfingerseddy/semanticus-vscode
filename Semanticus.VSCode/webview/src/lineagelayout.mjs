// The Lineage graph's own force layout. A plain ES module (the tabbusy.mjs pattern) so the node test runs the SAME code
// the webview ships.
//
// Why it exists: ECharts' built-in force layout starts every node at a RANDOM point and settles on the main thread in one
// pass, O(n squared) per step. On a model with 800 things that is a freeze of many seconds, and no two draws came out
// alike. This layout fixes both:
//   * the start is seeded from the node's own ref and home table (no Math.random), so the same model draws the same way;
//   * repulsion looks only at nearby nodes (a uniform grid), so a step costs about n, not n squared;
//   * it runs in short slices and gives the thread back between them, so the page keeps answering.
// The result depends only on the input, never on how the slices fell (the step count is fixed, not timed).

const hash = (s) => { let h = 2166136261; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); } return h >>> 0; };
const GOLDEN = Math.PI * (3 - Math.sqrt(5));

/** Seeded start: one cluster per home table on a sunflower spiral, nodes inside a cluster on a smaller one.
 *  `nodes` = [{ id, group }], returns Float64Array [x0, y0, x1, y1, ...] in node order. */
export function seedPositions(nodes, spread = 38) {
  const groups = [...new Set(nodes.map((n) => n.group))].sort();
  const members = new Map(groups.map((g) => [g, []]));
  for (const n of nodes) members.get(n.group).push(n.id);
  for (const list of members.values()) list.sort();
  const centre = new Map(groups.map((g, i) => {
    const r = spread * 2.2 * Math.sqrt(i + 0.5), a = i * GOLDEN + (hash(g) % 1000) / 1000 * 0.2;
    return [g, [Math.cos(a) * r, Math.sin(a) * r]];
  }));
  const rank = new Map();
  for (const list of members.values()) list.forEach((id, i) => rank.set(id, i));
  const out = new Float64Array(nodes.length * 2);
  nodes.forEach((n, i) => {
    const k = rank.get(n.id), c = centre.get(n.group), r = spread * 0.55 * Math.sqrt(k + 0.5), a = k * GOLDEN;
    out[2 * i] = c[0] + Math.cos(a) * r; out[2 * i + 1] = c[1] + Math.sin(a) * r;
  });
  return out;
}

/** A layout in progress. `step(budgetMs)` runs whole iterations until the budget is spent (always at least one) and
 *  returns true when all are done. `positions` is the live Float64Array. */
export function createLayout({ nodes, links, iterations, edgeLength = 46, repulsion = 900, cutoff = 120, gravity = 0.012 }) {
  const n = nodes.length;
  const index = new Map(nodes.map((d, i) => [d.id, i]));
  const pos = seedPositions(nodes);
  const vel = new Float64Array(n * 2);
  const edges = [];
  for (const l of links) { const a = index.get(l.source), b = index.get(l.target); if (a != null && b != null && a !== b) edges.push(a, b); }
  const total = iterations ?? Math.max(60, Math.min(220, Math.round(40000 / Math.max(1, n) + 60)));
  let it = 0;
  const cell = cutoff;

  function iterate() {
    const alpha = 1 - it / total;                       // cools to nothing so the picture comes to rest
    const force = new Float64Array(n * 2);
    // repulsion: bucket into a grid, look only at the 3x3 cells around a node
    const grid = new Map();
    for (let i = 0; i < n; i++) {
      const key = Math.floor(pos[2 * i] / cell) * 100003 + Math.floor(pos[2 * i + 1] / cell);
      const b = grid.get(key); if (b) b.push(i); else grid.set(key, [i]);
    }
    for (let i = 0; i < n; i++) {
      const cx = Math.floor(pos[2 * i] / cell), cy = Math.floor(pos[2 * i + 1] / cell);
      for (let dx = -1; dx <= 1; dx++) for (let dy = -1; dy <= 1; dy++) {
        const b = grid.get((cx + dx) * 100003 + (cy + dy)); if (!b) continue;
        for (const j of b) {
          if (j <= i) continue;
          let ex = pos[2 * i] - pos[2 * j], ey = pos[2 * i + 1] - pos[2 * j + 1];
          let d2 = ex * ex + ey * ey;
          if (d2 > cutoff * cutoff) continue;
          if (d2 < 0.01) { ex = ((i * 7 + j) % 5) - 2 || 1; ey = ((i + j * 3) % 5) - 2 || 1; d2 = ex * ex + ey * ey; }
          const d = Math.sqrt(d2), f = repulsion / d2, fx = (ex / d) * f, fy = (ey / d) * f;
          force[2 * i] += fx; force[2 * i + 1] += fy; force[2 * j] -= fx; force[2 * j + 1] -= fy;
        }
      }
    }
    // springs along links, then a weak pull to the middle so loose groups stay in view
    for (let e = 0; e < edges.length; e += 2) {
      const a = edges[e], b = edges[e + 1];
      const ex = pos[2 * b] - pos[2 * a], ey = pos[2 * b + 1] - pos[2 * a + 1];
      const d = Math.sqrt(ex * ex + ey * ey) || 0.01, f = (d - edgeLength) * 0.06;
      const fx = (ex / d) * f, fy = (ey / d) * f;
      force[2 * a] += fx; force[2 * a + 1] += fy; force[2 * b] -= fx; force[2 * b + 1] -= fy;
    }
    for (let i = 0; i < n; i++) {
      force[2 * i] -= pos[2 * i] * gravity; force[2 * i + 1] -= pos[2 * i + 1] * gravity;
      vel[2 * i] = (vel[2 * i] + force[2 * i]) * 0.6; vel[2 * i + 1] = (vel[2 * i + 1] + force[2 * i + 1]) * 0.6;
      const speed = Math.hypot(vel[2 * i], vel[2 * i + 1]), cap = 30 * alpha + 2;
      const k = speed > cap ? cap / speed : 1;
      pos[2 * i] += vel[2 * i] * k * alpha; pos[2 * i + 1] += vel[2 * i + 1] * k * alpha;
    }
    it++;
  }

  return {
    positions: pos,
    get iteration() { return it; },
    get total() { return total; },
    step(budgetMs = 8, now = () => performance.now()) {
      const t0 = now();
      do { if (it >= total) break; iterate(); } while (now() - t0 < budgetMs);
      return it >= total;
    },
  };
}

/** Run a layout to the end in slices, giving the thread back between them. `onFrame(positions, done)` is called at the
 *  start (the seeded picture) and then now and again, and once more when done. How often is paced by what a frame
 *  costs: the gap between handing a frame over and getting the thread back is the cost of drawing it, and the next frame
 *  waits at least five times that, so a slow machine spends most of its time laying out, not repainting. With
 *  `intermediate: false` only the seeded picture and the finished one are handed over (a small graph settles in a moment).
 *  Returns a cancel function. */
export function runLayoutSliced(layout, onFrame, { sliceMs = 8, frameMs = 450, intermediate = true, yieldFn = (fn) => setTimeout(fn, 0), now = () => performance.now() } = {}) {
  let live = true, lastFrame = now(), handedAt = -1, wait = frameMs;
  onFrame(layout.positions, false);
  const tick = () => {
    if (!live) return;
    if (handedAt >= 0) { wait = Math.max(frameMs, (now() - handedAt) * 5); handedAt = -1; }
    const done = layout.step(sliceMs, now);
    if (done) { onFrame(layout.positions, true); return; }
    if (intermediate && now() - lastFrame >= wait) { lastFrame = now(); handedAt = lastFrame; onFrame(layout.positions, false); }
    yieldFn(tick);
  };
  yieldFn(tick);
  return () => { live = false; };
}
