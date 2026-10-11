// The routed bus matrix stays responsive at every model size Kane named (27 Sep ruling: lines route through the
// gaps, never behind or through a card, and the page never freezes). Lays out 1, 5, 11, 30, 80, 150 and 300-table models
// with the SAME placement the webview ships (busGrid), anchors them the way computeAnchors does (a fact joins on its
// right edge, a lookup table on its bottom edge, ends spread evenly), and times routeAll, the one routing pass the
// Diagram runs per layout change. Then checks what the page would draw: every line routed, none through a card,
// none on top of another. The drag case is timed too: one card moved, the rest of the routes kept.
import assert from 'node:assert/strict';
import { busGrid, routeAll, sharedLength, pathOf } from '../webview/src/diagramroute.mjs';

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};
const SLOT = 30, NODE_W = 190, NODE_H = 92;

// A model of n tables: about one fact in ten, one unlinked in seven, the rest lookup tables; each fact uses up to
// nine lookup tables, and a few lookup tables hang off others (snowflakes).
export function model(n) {
  const nf = Math.max(1, Math.round(n / 10)), ni = n > 4 ? Math.round(n / 7) : 0, nd = Math.max(0, n - nf - ni);
  const facts = Array.from({ length: nf }, (_, i) => 'F' + i), dims = Array.from({ length: nd }, (_, i) => 'D' + i), iso = Array.from({ length: ni }, (_, i) => 'L' + i);
  const rels = [], seen = new Set();
  const add = (m, o) => { const k = m + '>' + o; if (m !== o && !seen.has(k)) { seen.add(k); rels.push([m, o]); } };
  facts.forEach((f, fi) => { for (let k = 0; k < Math.min(9, nd); k++) add(f, dims[(fi * 7 + k * 5) % nd]); });
  for (let i = 0; i + 1 < nd && i < 16; i += 2) add(dims[i], dims[nd - 1 - i]);
  return { facts, dims, iso, rels };
}

// Placement and anchors as the webview does them, for a canvas of the given shape. `open` gives every card its
// open height (its column list shown), which differs card to card, so the rows are ragged and the channels crowded.
export function scene(n, aspect, open = false) {
  const { facts, dims, iso, rels } = model(n);
  const cnt = (t, side) => rels.filter(([m, o]) => (side === 'right' ? m === t : o === t)).length;
  const rows = (t) => 3 + (Number(t.slice(1)) % 7) * 2;   // the harness models' column counts
  const size = (t) => ({ w: Math.max(NODE_W, (cnt(t, 'bottom') + 1) * SLOT), h: Math.max(NODE_H, (cnt(t, 'right') + 1) * SLOT, open ? 70 + rows(t) * 18 : 0) });
  const g = busGrid(dims.map(size), facts.map(size), iso.map(size), aspect);
  const box = {};
  dims.forEach((d, i) => { box[d] = { id: d, ...g.dims[i], ...size(d) }; });
  facts.forEach((f, i) => { box[f] = { id: f, ...g.facts[i], ...size(f) }; });
  iso.forEach((t, i) => { box[t] = { id: t, ...g.iso[i], ...size(t) }; });
  const used = {};
  const slot = (t, side) => (used[t + side] = (used[t + side] ?? 0) + 1);
  const lines = rels.map(([m, o]) => {
    const M = box[m], O = box[o];
    return { id: `${m}>${o}`, src: { x: M.x + M.w + 4, y: M.y + M.h * slot(m, 'right') / (cnt(m, 'right') + 1), side: 'right' },
      dst: { x: O.x + O.w * slot(o, 'bottom') / (cnt(o, 'bottom') + 1), y: O.y + O.h + 4, side: 'bottom' } };
  });
  return { boxes: Object.values(box), lines, box };
}

const through = (pts, boxes) => {
  const out = [];
  for (let i = 1; i < pts.length; i++) for (const b of boxes) {
    const a = pts[i - 1], c = pts[i];
    if (Math.min(a.x, c.x) < b.x + b.w && Math.max(a.x, c.x) > b.x && Math.min(a.y, c.y) < b.y + b.h && Math.max(a.y, c.y) > b.y) out.push(b.id);
  }
  return out;
};
const onTop = (routes) => {
  const ids = [...routes.keys()], out = [];
  for (let i = 0; i < ids.length; i++) for (let j = i + 1; j < ids.length; j++) {
    const run = sharedLength(routes.get(ids[i]).pts, routes.get(ids[j]).pts);
    if (run > 8) out.push(`${ids[i]} and ${ids[j]} share ${Math.round(run)} px`);
  }
  return out;
};
const time = (fn) => { const t0 = performance.now(); const r = fn(); return [r, performance.now() - t0]; };

// Budgets are for one full routing pass, and several times the measured cost on a loaded laptop (see the report),
// so a busy machine cannot trip them while the old per-line, per-render routing (1.1 to 2 s at 80) still would.
const BUDGET = { 1: 50, 5: 50, 11: 100, 30: 250, 80: 600, 150: 1500, 300: 4000 };
// The drag budgets were 20 to 200 ms; on 28 Sep the Inspiron at load 28 to 50 tripped the 11-table one at 32 ms and the
// 80-table one at 109 ms, while the best of five drag frames there is 4 to 16 ms (old and new router alike). They now sit
// where a swamped machine cannot reach, and still far below the old per-line routing's whole seconds per drag step.
const DRAG_BUDGET = { 1: 100, 5: 100, 11: 100, 30: 150, 80: 300, 150: 500, 300: 800 };
for (const n of [1, 5, 11, 30, 80, 150, 300]) for (const [aspect, open] of [[1092 / 606, false], [627 / 606, false], [1092 / 606, true]]) {
  ok(`${n} tables, canvas shape ${aspect.toFixed(2)}${open ? ', every card open' : ''}: every line routed round the cards, none on top of another, in time`, () => {
    const { boxes, lines, box } = scene(n, aspect, open);
    routeAll(lines, boxes);   // warm the JIT, as a page that already drew once has
    const [routes, ms] = time(() => routeAll(lines, boxes));
    const missing = lines.filter((l) => !routes.get(l.id).pts).map((l) => l.id);
    assert.deepEqual(missing, [], 'lines with no route');
    const bad = lines.flatMap((l) => through(routes.get(l.id).pts, boxes).map((b) => `${l.id} through ${b}: ${pathOf(routes.get(l.id).pts)}`));
    assert.deepEqual(bad, [], 'lines through a card');
    assert.deepEqual(onTop(routes), [], 'lines drawn on top of each other');
    assert.ok(ms < BUDGET[n], `one routing pass took ${ms.toFixed(1)} ms (budget ${BUDGET[n]} ms)`);
    // A drag frame: the first fact moves 30 px right; only the lines it disturbs are routed again.
    const f = box.F0, moved = boxes.map((b) => (b === f ? { ...b, x: b.x + 30 } : b));
    const dragged = lines.map((l) => (l.id.startsWith('F0>') ? { ...l, src: { ...l.src, x: l.src.x + 30 } } : l));
    const [again, dms] = time(() => routeAll(dragged, moved, routes));
    assert.ok(dragged.every((l) => again.get(l.id).pts), 'every line still routed after the drag');
    assert.deepEqual(dragged.flatMap((l) => through(again.get(l.id).pts, moved).map((b) => l.id + ' through ' + b)), [], 'after the drag, lines through a card');
    assert.ok(dms < DRAG_BUDGET[n], `one drag frame took ${dms.toFixed(1)} ms (budget ${DRAG_BUDGET[n]} ms)`);
    console.log(`    ${n} tables: ${lines.length} lines, one pass ${ms.toFixed(1)} ms, one drag frame ${dms.toFixed(1)} ms`);
  });
}

console.log(`diagram-route-scale: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
