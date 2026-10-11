// verify: the bus matrix's routed lines must not be drawn on top of each other (m11/diagram-route verification,
// 27 Sep). Left failing on purpose for the builder.
//
// The Diagram's older rule, which the routing lane was asked to keep: every relationship line gets its own lane, so
// a person can follow each one ("lines on top of each other" was the complaint the anchor spread and the dagre
// separation fixed, diagram.tsx:56 and :245). The single-row bus matrix met it: its L-shaped lines leave from
// distinct anchors, so no two share a straight run (the measured base, b0c7095c, has 0 such pairs for the 11-table
// model). The wrapped layout routes lines through shared channels, but diagramroute.mjs only offers 4 tracks and
// assigns them by edge index (track % 4), so the 1st, 5th and 9th lines of a channel land on the same track and are
// drawn over each other. On the live canvas (11 tables, 900 x 900) Sales -> Date and the inactive, dashed Returns ->
// Customer share 293 px of one line, which hides the dashed line; the scene below reproduces that exact number.
//
// The geometry is the bus matrix's own (busMatrixPositions: COL_GAP 96, ROW_GAP 110, facts GAP 72; computeAnchors:
// the fact joins on its right edge, the lookup table on its bottom edge, anchors spread evenly) for harness.html's
// ?g=eleven model wrapped into 3 and 4 columns, routed by the SAME module the webview ships, one track per edge index
// as displayEdges (diagram.tsx:1222) assigns them.
import assert from 'node:assert/strict';
import { routeOrthogonal, pathOf } from '../webview/src/diagramroute.mjs';

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};

const NODE_W = 190, NODE_H = 92, SLOT = 30, COL_GAP = 96, ROW_GAP = 110, GAP = 72;
const RELS = [['Sales', 'Date'], ['Sales', 'Product'], ['Sales', 'Customer'], ['Sales', 'Store'], ['Sales', 'Promotion'], ['Sales', 'Currency'], ['Sales', 'Channel'],
  ['Inventory', 'Date'], ['Inventory', 'Product'], ['Inventory', 'Store'], ['Returns', 'Date'], ['Returns', 'Product'], ['Returns', 'Customer'], ['Returns', 'Channel'], ['Customer', 'Geography']];
const DIMS = ['Date', 'Product', 'Customer', 'Store', 'Channel', 'Promotion', 'Currency', 'Geography'];
const FACTS = ['Inventory', 'Returns', 'Sales'];

function scene(cols) {
  const count = (t, side) => RELS.filter(([f, d]) => (side === 'right' ? f === t : d === t)).length;
  const box = {};
  DIMS.forEach((d, i) => { box[d] = { id: d, x: NODE_W + COL_GAP + (i % cols) * (NODE_W + COL_GAP), y: Math.floor(i / cols) * (NODE_H + ROW_GAP), w: Math.max(NODE_W, (count(d, 'bottom') + 1) * SLOT), h: NODE_H }; });
  let y = NODE_H + ROW_GAP;
  for (const f of FACTS) { const h = Math.max(NODE_H, (count(f, 'right') + 1) * SLOT); box[f] = { id: f, x: 0, y, w: NODE_W, h }; y += h + GAP; }
  const used = {};
  const slot = (t, side) => (used[t + side] = (used[t + side] ?? 0) + 1);
  return RELS.map(([f, d], i) => {
    const F = box[f], D = box[d];
    const src = { x: F.x + F.w, y: F.y + F.h * slot(f, 'right') / (count(f, 'right') + 1), side: 'right' };
    const dst = { x: D.x + D.w * slot(d, 'bottom') / (count(d, 'bottom') + 1), y: D.y + D.h, side: 'bottom' };
    return { id: `${f} -> ${d}`, pts: routeOrthogonal(src, dst, Object.values(box), i) };
  });
}

// The length two routes run along the same straight line (collinear, overlapping spans), in flow px.
function sharedRun(A, B) {
  let run = 0;
  for (let i = 1; i < A.length; i++) for (let j = 1; j < B.length; j++) {
    const a = A[i - 1], b = A[i], c = B[j - 1], d = B[j];
    if (a.x === b.x && c.x === d.x && a.x === c.x) run += Math.max(0, Math.min(Math.max(a.y, b.y), Math.max(c.y, d.y)) - Math.max(Math.min(a.y, b.y), Math.min(c.y, d.y)));
    if (a.y === b.y && c.y === d.y && a.y === c.y) run += Math.max(0, Math.min(Math.max(a.x, b.x), Math.max(c.x, d.x)) - Math.max(Math.min(a.x, b.x), Math.min(c.x, d.x)));
  }
  return run;
}
function onTopOfEachOther(lines) {
  const out = [];
  for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {
    const run = sharedRun(lines[i].pts, lines[j].pts);
    if (run > 8) out.push(`${lines[i].id} and ${lines[j].id} share ${Math.round(run)} px (${pathOf(lines[i].pts)} | ${pathOf(lines[j].pts)})`);
  }
  return out;
}

ok('the single row (the old layout) draws no two lines on top of each other (control)', () => {
  const lines = scene(DIMS.length);
  assert.ok(lines.every((l) => l.pts), 'every line routed');
  assert.deepEqual(onTopOfEachOther(lines), []);
});

for (const cols of [3, 4]) {
  ok(`wrapped into ${cols} columns, no two lines are drawn on top of each other`, () => {
    const lines = scene(cols);
    assert.ok(lines.every((l) => l.pts), 'every line routed');
    const bad = onTopOfEachOther(lines);
    assert.equal(bad.length, 0, `${bad.length} pairs of lines share a straight run over 8 px:\n${bad.join('\n')}`);
  });
}

console.log(`diagram-route-lanes: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
