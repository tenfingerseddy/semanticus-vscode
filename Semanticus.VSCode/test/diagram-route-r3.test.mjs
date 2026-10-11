// Review round 3 of m11/diagram-route (28 Sep): the router's answers to the round 2 verification, each written to fail
// on the round 2 router (fbee7b10) first. Executes the SAME router the webview ships (webview/src/diagramroute.mjs).
// Kane's two rulings are the bar: no line ever runs behind a card, and no two lines are drawn on top of each other.
//
// 1. A card 4 to 13 px beyond a handle, on any of the four sides, sent the line straight through it: the stub check
//    excused every card whose grown box held the handle, a close neighbour's too. Now only the line's own card is.
// 2. The same excuse let a drag KEEP such a route: a card moved 8 px under a handle left the old line running through it.
//    And a card at exactly 4 px (float noise put the handle inside it) or covering a handle by under 4 px let the line
//    run under all of that card: now the card is cut at the handle and the line leaves by the slit.
// 3. Lines drawn on top of each other in a tidy grid the person's assistant saves (30, 80 and 150 tables 40 px
//    apart, and the live page's own 80-table grid), and in the Studio's own layout with every card open at 300 tables
//    (the live page's geometry). The spread and the second routing pass in routeAll pull them apart.
// 4. holdRoutes: what the Diagram draws while a burst of re-flow waits for its pass, so no line vanishes.
// 5. clearDropped: a card dropped half over another slides clear of it, and no other card moves (UAT-S11-1-004).
// "On top of each other" is the router's own measure: sharedLength (within NEAR px, the same way) over 8 px.
import assert from 'node:assert/strict';
import * as R from '../webview/src/diagramroute.mjs';
import * as F from '../webview/src/diagramfit.mjs';

const { routeAll, routeOrthogonal, sharedLength, pathOf, MARGINS } = R;
let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};
// The test's own crossing check: every segment against every card, strictly inside.
const through = (pts, boxes) => {
  const out = [];
  for (let i = 1; i < pts.length; i++) for (const b of boxes) {
    const a = pts[i - 1], c = pts[i];
    if (Math.min(a.x, c.x) < b.x + b.w && Math.max(a.x, c.x) > b.x && Math.min(a.y, c.y) < b.y + b.h && Math.max(a.y, c.y) > b.y) out.push(b.id ?? `${b.x},${b.y}`);
  }
  return out;
};
const onTop = (r, lines) => {
  const out = [];
  for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {
    const a = r.get(lines[i].id).pts, b = r.get(lines[j].id).pts;
    if (a && b && sharedLength(a, b) > 8) out.push(`${lines[i].id} runs ${Math.round(sharedLength(a, b))} px along ${lines[j].id}`);
  }
  return out;
};
const checkAll = (r, lines, boxes) => {
  assert.deepEqual(lines.filter((l) => !r.get(l.id).pts).map((l) => l.id), [], 'lines with no route');
  assert.deepEqual(lines.flatMap((l) => through(r.get(l.id).pts, boxes).map((b) => `${l.id} through ${b}: ${pathOf(r.get(l.id).pts)}`)), [], 'lines through a card');
};

// ---- 1 and 2: a card close beyond a handle, on every side ----
// The verifier's scene (a lookup table with another 4 to 13 px under it, more cards further down), and the same scene
// mirrored and turned, so the handle faces down, up, right and left.
const turn = {
  bottom: { p: (x, y) => ({ x, y }), b: (r) => r, side: (s) => s },
  top: { p: (x, y) => ({ x, y: -y }), b: (r) => ({ ...r, y: -(r.y + r.h) }), side: (s) => ({ top: 'bottom', bottom: 'top' }[s] ?? s) },
  right: { p: (x, y) => ({ x: y, y: x }), b: (r) => ({ ...r, x: r.y, y: r.x, w: r.h, h: r.w }), side: (s) => ({ top: 'left', bottom: 'right', left: 'top', right: 'bottom' }[s]) },
  left: { p: (x, y) => ({ x: -y, y: x }), b: (r) => ({ ...r, x: -(r.y + r.h), y: r.x, w: r.h, h: r.w }), side: (s) => ({ top: 'right', bottom: 'left', left: 'top', right: 'bottom' }[s]) },
};
function closeScene(gap, t) {
  const boxes = [{ id: 'Date', x: 286, y: 0, w: 190, h: 92 }, { id: 'Promotion', x: 286, y: 92 + gap, w: 190, h: 92 },
    { id: 'Store', x: 286, y: 500, w: 190, h: 92 }, { id: 'Sales', x: 0, y: 250, w: 190, h: 150 }].map(t.b);
  const end = (x, y, side) => ({ ...t.p(x, y), side: t.side(side) });
  const lines = [{ id: 'Sales>Date', src: end(194, 300, 'right'), dst: end(381, 96, 'bottom') },
    { id: 'Sales>Store', src: end(194, 350, 'right'), dst: end(381, 596, 'bottom') }];
  return { boxes, lines };
}
ok('a card 4 to 13 px beyond a handle, on any side: no line runs through it (routeAll and routeOrthogonal)', () => {
  const bad = [];
  for (const [name, t] of Object.entries(turn)) for (const gap of [4, 6, 8, 10, 12, 13]) {
    const { boxes, lines } = closeScene(gap, t);
    const r = routeAll(lines, boxes);
    for (const l of lines) {
      for (const [how, pts] of [['routeAll', r.get(l.id).pts], ['routeOrthogonal', routeOrthogonal(l.src, l.dst, boxes)]]) {
        if (!pts) { bad.push(`${name} ${gap} px: ${l.id} has no route (${how})`); continue; }
        const hit = through(pts, boxes);
        if (hit.length) bad.push(`${name} ${gap} px: ${l.id} runs through ${hit.join(', ')} (${how}): ${pathOf(pts)}`);
      }
    }
  }
  assert.deepEqual(bad, []);
});

ok('a drag keeps no route that a card moved 8 px beyond its handle now covers', () => {
  const bad = [];
  for (const [name, t] of Object.entries(turn)) {
    const far = closeScene(300, t), near = closeScene(8, t);
    const first = routeAll(far.lines, far.boxes);
    // The drag moved Promotion up under Date; the lines' own ends did not move, so their routes are candidates to keep.
    const next = routeAll(near.lines, near.boxes, first);
    for (const l of near.lines) {
      const hit = through(next.get(l.id).pts, near.boxes);
      if (hit.length) bad.push(`${name}: ${l.id} kept through ${hit.join(', ')}: ${pathOf(next.get(l.id).pts)}`);
    }
  }
  assert.deepEqual(bad, []);
});

// The live drive's own find: a card the assistant saves exactly 4 px beside a handle put the handle 0.00001 px inside
// it (a measured card's float noise), so the router took the handle for covered and dropped that card, and the line ran
// 190 px under it. And a card that really covers a handle, by under 4 px, let the line run under all of it.
ok('a card at 4 px (with float noise) or covering a handle by under 4 px: the line leaves by the slit, not under the card', () => {
  const bad = [];
  const under = (pts, c) => {
    let run = 0;
    for (let i = 1; i < pts.length; i++) {
      const a = pts[i - 1], b = pts[i];
      if (a.x === b.x && a.x > c.x && a.x < c.x + c.w) run += Math.max(0, Math.min(Math.max(a.y, b.y), c.y + c.h) - Math.max(Math.min(a.y, b.y), c.y));
      if (a.y === b.y && a.y > c.y && a.y < c.y + c.h) run += Math.max(0, Math.min(Math.max(a.x, b.x), c.x + c.w) - Math.max(Math.min(a.x, b.x), c.x));
    }
    return run;
  };
  for (const [name, t] of Object.entries(turn)) for (const gap of [4 - 1e-5, 3.7, 3, 2]) {
    const own = t.b({ id: 'Own', x: 0, y: 0, w: 190, h: 92 }), cover = t.b({ id: 'Cover', x: 40, y: 92 + gap, w: 190, h: 92 });
    const far = t.b({ id: 'Far', x: 700, y: 400, w: 190, h: 92 });
    const src = { ...t.p(95, 96), side: t.side('bottom') }, dst = { ...t.p(696, 446), side: t.side('left') };
    const pts = routeAll([{ id: 'a', src, dst }], [own, cover, far]).get('a').pts;
    if (!pts) { bad.push(`${name} ${gap} px: no route`); continue; }
    const run = under(pts, cover), allowed = 4 - gap + 0.02;
    if (run > allowed) bad.push(`${name} ${+gap.toFixed(5)} px: ${Math.round(run)} px under the covering card (at most ${allowed.toFixed(2)}): ${pathOf(pts)}`);
    const other = through(pts, [own, far]);
    if (other.length) bad.push(`${name} ${gap} px: through ${other.join(', ')}`);
  }
  assert.deepEqual(bad, []);
});

// ---- 3: busy channels ----
// A tidy grid the person's assistant might save: facts first (tall, nine lines each off the right edge), then the
// lookup tables (lines into the bottom edge, spread evenly), then the unlinked tables, `gap` px apart, as many
// columns as suit the canvas.
function assistantGrid(nFacts, nDims, nIso, perFact, gap, aspect = 1.6) {
  const cards = [];
  for (let i = 0; i < nFacts; i++) cards.push({ id: 'F' + i, w: 190, h: 300 });
  for (let i = 0; i < nDims; i++) cards.push({ id: 'D' + i, w: 190, h: 92 });
  for (let i = 0; i < nIso; i++) cards.push({ id: 'L' + i, w: 190, h: 92 });
  const cols = Math.max(1, Math.round(Math.sqrt(cards.length * aspect * 300 / 190)));
  cards.forEach((c, i) => { c.x = (i % cols) * (190 + gap); c.y = Math.floor(i / cols) * (300 + gap); });
  const rels = [];
  for (let f = 0; f < nFacts; f++) for (let k = 0; k < perFact; k++) rels.push([f, (f * 7 + k * 5) % nDims]);
  const into = new Map(), seen = new Map();
  for (const [, d] of rels) into.set(d, (into.get(d) ?? 0) + 1);
  const lines = rels.map(([f, d], i) => {
    const F = cards[f], D = cards[nFacts + d], k = seen.get(d) ?? 0;
    seen.set(d, k + 1);
    return { id: `F${f}>D${d}#${i}`, src: { x: F.x + F.w + 4, y: F.y + 30 * ((i % perFact) + 1), side: 'right' },
      dst: { x: D.x + (D.w * (k + 1)) / (into.get(d) + 1), y: D.y + D.h + 4, side: 'bottom' } };
  });
  return { boxes: cards, lines };
}
// Round 4 (Kane, 28 Sep): the page makes room before it routes a layout that is too tight for its lines, and a grid
// 40 px apart is (F.ROOM_MIN is 48). So these grids are checked the way the page draws them: room made first, the
// line ends moved with their cards, then routed. The router alone in a 40 px grid had to choose between two lines
// 2 to 3 px apart and a line on a card's border (review round 4); with room made it needs neither.
function madeRoom({ boxes, lines }) {
  const cards = boxes.map((b, i) => ({ id: b.id ?? 'box' + i, x: b.x, y: b.y, w: b.w, h: b.h }));
  const own = (e) => cards.find((c) => (e.side === 'right' ? Math.abs(e.x - c.x - c.w) <= 6 && e.y >= c.y && e.y <= c.y + c.h
    : e.side === 'left' ? Math.abs(e.x - c.x) <= 6 && e.y >= c.y && e.y <= c.y + c.h
    : e.side === 'bottom' ? Math.abs(e.y - c.y - c.h) <= 6 && e.x >= c.x && e.x <= c.x + c.w
    : Math.abs(e.y - c.y) <= 6 && e.x >= c.x && e.x <= c.x + c.w));
  const counts = new Map(cards.map((c) => [c.id, { top: 0, right: 0, bottom: 0, left: 0 }]));
  const ends = lines.map((l) => [own(l.src), own(l.dst)]);
  ends.forEach(([a, b], i) => { if (a) counts.get(a.id)[lines[i].src.side]++; if (b) counts.get(b.id)[lines[i].dst.side]++; });
  const to = F.makeRoom(cards, counts);
  const by = (c) => (c && to[c.id] ? { dx: to[c.id].x - c.x, dy: to[c.id].y - c.y } : { dx: 0, dy: 0 });
  const mv = (e, c) => { const d = by(c); return { ...e, x: e.x + d.dx, y: e.y + d.dy }; };
  return {
    boxes: cards.map((c) => ({ ...c, ...(to[c.id] ?? {}) })),
    lines: lines.map((l, i) => ({ ...l, src: mv(l.src, ends[i][0]), dst: mv(l.dst, ends[i][1]) })),
  };
}
// Runs within HUG px of a card border they do not end on, along it for over 8 px: drawn on the border.
const onBorder = (r, lines, boxes) => {
  const out = [];
  for (const l of lines) {
    const pts = r.get(l.id).pts; if (!pts) continue;
    for (let i = 1; i < pts.length; i++) for (const b of boxes) {
      const a = pts[i - 1], c = pts[i];
      const v = a.x === c.x, at = v ? a.x : a.y, e0 = v ? b.x : b.y, e1 = e0 + (v ? b.w : b.h);
      const lo = Math.max(Math.min(v ? a.y : a.x, v ? c.y : c.x), v ? b.y : b.x), hi = Math.min(Math.max(v ? a.y : a.x, v ? c.y : c.x), v ? b.y + b.h : b.x + b.w);
      if ((Math.abs(at - e0) < R.HUG || Math.abs(at - e1) < R.HUG) && hi - lo > 8) out.push(`${l.id} along a card at ${v ? 'x' : 'y'} ${at} for ${Math.round(hi - lo)} px`);
    }
  }
  return out;
};
for (const [n, args] of [[30, [4, 20, 6, 7, 40]], [80, [8, 60, 12, 9, 40]], [150, [15, 113, 22, 9, 40]]]) {
  ok(`${n} tables in an assistant's grid 40 px apart, room made: every line round the cards, none on top of another or on a card's border`, () => {
    const { boxes, lines } = madeRoom(assistantGrid(...args));
    const r = routeAll(lines, boxes);
    checkAll(r, lines, boxes);
    assert.deepEqual(onTop(r, lines), [], 'lines drawn on top of each other');
    assert.deepEqual(onBorder(r, lines, boxes).slice(0, 4), [], 'lines drawn on a card border');
  });
}

const unpack = (fix) => ({
  boxes: fix.boxes.map(([x, y, w, h]) => ({ x, y, w, h })),
  lines: fix.lines.map(([sx, sy, ss, tx, ty, ts], i) => ({ id: 'L' + i, src: { x: sx, y: sy, side: ss }, dst: { x: tx, y: ty, side: ts } })),
});
// The live page's geometry (the round 2 verifier's harness dumps, 1440 x 900): the 80-table model in a plain grid
// 40 px apart, and the Studio's own bus matrix at 300 tables with every card open.
const GRID80 = {"boxes":[[230,0,190,300],[460,0,190,300],[690,0,190,300],[920,0,190,300],[1150,0,190,300],[1380,0,190,300],[1610,0,190,300],[1840,0,190,300],[2070,0,190,92],[2300,0,190,92],[2530,0,190,92],[2760,0,190,92],[2990,0,190,92],[0,340,190,92],[0,0,190,92],[230,340,190,92],[460,340,190,92],[690,340,190,92],[920,340,190,92],[1610,1360,190,92],[1150,340,190,92],[1840,1360,190,92],[1380,340,190,92],[1610,340,190,92],[2070,1360,190,92],[1840,340,190,92],[2300,1360,190,92],[2070,340,190,92],[2300,340,190,92],[2530,340,190,92],[2760,340,190,92],[2530,1360,190,92],[2990,340,190,92],[0,680,190,92],[230,680,190,92],[460,680,190,92],[690,680,190,92],[920,680,190,92],[1150,680,190,92],[1380,680,190,92],[1610,680,190,92],[1840,680,190,92],[2070,680,190,92],[2300,680,190,92],[2530,680,190,92],[2760,680,190,92],[2990,680,190,92],[0,1020,190,92],[230,1020,190,92],[460,1020,190,92],[690,1020,190,92],[920,1020,190,92],[1150,1020,190,92],[1380,1020,190,92],[1610,1020,190,92],[1840,1020,190,92],[2070,1020,190,92],[2300,1020,190,92],[2530,1020,190,92],[2760,1020,190,92],[2990,1020,190,92],[0,1360,190,92],[230,1360,190,92],[460,1360,190,92],[690,1360,190,92],[920,1360,190,92],[1150,1360,190,92],[1380,1360,190,92],[2760,1360,190,92],[2990,1360,190,92],[0,1700,190,92],[230,1700,190,92],[460,1700,190,92],[690,1700,190,92],[920,1700,190,92],[1150,1700,190,92],[1380,1700,190,92],[1610,1700,190,92],[1840,1700,190,92],[2070,1700,190,92]],"lines":[[424,30,"right",2196.657,96,"bottom"],[424,90,"right",126.656,436,"bottom"],[424,60,"right",1046.657,436,"bottom"],[424,150,"right",1736.657,436,"bottom"],[424,120,"right",2395.001,436,"bottom"],[424,180,"right",95,776,"bottom"],[424,240,"right",1245.001,776,"bottom"],[424,210,"right",2426.657,776,"bottom"],[424,270,"right",356.656,1116,"bottom"],[654,120,"right",356.656,436,"bottom"],[654,60,"right",1276.657,436,"bottom"],[654,150,"right",1966.657,436,"bottom"],[654,180,"right",2886.657,436,"bottom"],[654,210,"right",555,776,"bottom"],[654,270,"right",1705.001,776,"bottom"],[654,30,"right",2855.001,776,"bottom"],[654,240,"right",816.657,1116,"bottom"],[654,90,"right",1982.501,1116,"bottom"],[884,90,"right",1506.657,436,"bottom"],[884,180,"right",2196.657,436,"bottom"],[884,210,"right",3116.658,436,"bottom"],[884,240,"right",1046.657,776,"bottom"],[884,270,"right",2165.001,776,"bottom"],[884,60,"right",95,1116,"bottom"],[884,150,"right",1245.001,1116,"bottom"],[884,120,"right",2442.501,1116,"bottom"],[884,30,"right",356.656,1456,"bottom"],[1114,150,"right",2625.001,436,"bottom"],[1114,210,"right",325,776,"bottom"],[1114,240,"right",1475,776,"bottom"],[1114,270,"right",2625.001,776,"bottom"],[1114,60,"right",555,1116,"bottom"],[1114,120,"right",1705,1116,"bottom"],[1114,30,"right",2823.329,1116,"bottom"],[1114,180,"right",785,1456,"bottom"],[1114,90,"right",2395.001,96,"bottom"],[1344.001,240,"right",785,776,"bottom"],[1344.001,270,"right",1935.001,776,"bottom"],[1344.001,60,"right",3085.001,776,"bottom"],[1344.001,90,"right",1015,1116,"bottom"],[1344.001,150,"right",2165.001,1116,"bottom"],[1344.001,30,"right",126.656,1456,"bottom"],[1344.001,210,"right",1245,1456,"bottom"],[1344.001,120,"right",2855.001,96,"bottom"],[1344.001,180,"right",555,436,"bottom"],[1574.001,210,"right",2363.329,776,"bottom"],[1574.001,240,"right",293.329,1116,"bottom"],[1574.001,270,"right",1506.657,1116,"bottom"],[1574.001,180,"right",2625.001,1116,"bottom"],[1574.001,90,"right",523.328,1456,"bottom"],[1574.001,30,"right",2133.329,96,"bottom"],[1574.001,120,"right",63.328,436,"bottom"],[1574.001,60,"right",983.328,436,"bottom"],[1574.001,150,"right",1673.329,436,"bottom"],[1804.001,240,"right",753.328,1116,"bottom"],[1804.001,60,"right",1935.001,1116,"bottom"],[1804.001,270,"right",3085.001,1116,"bottom"],[1804.001,120,"right",983.328,1456,"bottom"],[1804.001,90,"right",2625.001,96,"bottom"],[1804.001,150,"right",293.328,436,"bottom"],[1804.001,30,"right",1213.329,436,"bottom"],[1804.001,180,"right",1903.329,436,"bottom"],[1804.001,210,"right",2823.329,436,"bottom"],[2034.001,60,"right",2395.001,1116,"bottom"],[2034.001,120,"right",293.328,1456,"bottom"],[2034.001,240,"right",1506.657,1456,"bottom"],[2034.001,90,"right",3085.001,96,"bottom"],[2034.001,270,"right",785,436,"bottom"],[2034.001,30,"right",1443.329,436,"bottom"],[2034.001,150,"right",2133.329,436,"bottom"],[2034.001,180,"right",3053.33,436,"bottom"],[2034.001,210,"right",983.328,776,"bottom"],[2264.001,46,"right",1443.329,1456,"bottom"],[2724.001,46,"right",1046.657,1456,"bottom"],[3184.001,46,"right",586.656,1456,"bottom"],[194,46,"right",63.328,1456,"bottom"],[654,386,"right",2886.657,1116,"bottom"],[1114,386,"right",2347.501,1116,"bottom"],[1344.001,386,"right",1887.501,1116,"bottom"],[1574.001,386,"right",1443.328,1116,"bottom"]]};
const OPEN300 = {"boxes":[[0,202,190,303],[0,577,190,327],[0,1735,190,327],[0,2134,190,327],[0,2908,190,327],[0,2533,190,303],[0,3307,190,327],[0,3706,190,327],[0,4105,190,327],[0,4504,190,327],[0,4903,190,303],[0,5278,190,327],[0,5677,190,327],[0,6076,190,327],[0,6874,190,327],[0,7273,190,303],[0,7648,190,327],[0,8047,190,327],[0,8845,190,327],[0,9244,190,327],[0,9643,190,303],[0,10018,190,327],[0,10417,190,327],[0,11215,190,327],[0,11614,190,327],[0,12412,190,303],[0,12013,190,327],[0,10816,190,327],[0,8446,190,327],[0,6475,190,327],[286,0,930,173],[1312,0,190,238.5],[11894,0,190,287.5],[0,1336,190,327],[22726,0,190,327],[1598,0,190,327],[12180,0,190,327],[1884,0,190,189.5],[12466,0,190,238.5],[0,976,190,287.5],[22726,399,190,327],[2170,0,190,327],[12752,0,190,327],[2456,0,190,327],[22726,798,190,189.5],[2742,0,190,238.5],[13038,0,190,287.5],[22726,1060,190,327],[13324,0,190,327],[22726,1459,190,327],[13610,0,190,327],[13896,0,190,189.5],[14182,0,190,238.5],[14468,0,190,287.5],[22726,1858,190,327],[14754,0,190,327],[15040,0,190,327],[15326,0,190,327],[15612,0,190,189.5],[15898,0,190,238.5],[16184,0,190,287.5],[16470,0,190,327],[16756,0,190,327],[17042,0,190,327],[17328,0,190,327],[17614,0,190,189.5],[3028,0,190,238.5],[17900,0,190,287.5],[18186,0,190,327],[18472,0,190,327],[18758,0,190,327],[19044,0,190,327],[19330,0,190,189.5],[3314,0,190,238.5],[19616,0,190,287.5],[19902,0,190,327],[20188,0,190,327],[20474,0,190,327],[20760,0,190,327],[21046,0,190,189.5],[3600,0,190,238.5],[21332,0,190,287.5],[21618,0,190,327],[21904,0,190,327],[22190,0,190,327],[22476,0,190,327],[286,283,190,189.5],[3886,0,190,238.5],[1312,349,190,287.5],[1598,437,190,327],[1884,300,190,327],[2170,437,190,327],[2456,437,190,327],[2742,349,190,189.5],[4172,0,190,238.5],[3028,349,190,287.5],[3314,349,190,327],[3600,349,190,327],[3886,349,190,327],[4172,349,190,327],[4458,349,190,189.5],[4458,0,190,238.5],[4744,349,190,287.5],[5030,349,190,327],[5316,349,190,327],[5602,349,190,327],[5888,349,190,327],[6174,349,190,189.5],[4744,0,190,238.5],[6460,349,190,287.5],[6746,349,190,327],[7032,349,190,327],[7318,349,190,327],[7604,349,190,327],[7890,349,190,189.5],[5030,0,190,238.5],[8176,349,190,287.5],[8462,349,190,327],[8748,349,190,327],[9034,349,190,327],[9320,349,190,327],[9606,349,190,189.5],[5316,0,190,238.5],[9892,349,190,287.5],[10178,437,190,327],[10464,437,190,327],[10750,300,190,327],[11036,398,190,327],[11322,349,190,189.5],[5602,0,190,238.5],[11608,437,190,287.5],[11894,398,190,327],[12180,437,190,327],[12466,349,190,327],[12752,437,190,327],[13038,398,190,189.5],[5888,0,190,238.5],[13324,437,190,287.5],[13610,437,190,327],[13896,300,190,327],[14182,349,190,327],[14468,398,190,327],[14754,437,190,189.5],[6174,0,190,238.5],[15040,437,190,287.5],[15326,437,190,327],[15612,300,190,327],[15898,349,190,327],[16184,398,190,327],[16470,437,190,189.5],[6460,0,190,238.5],[16756,437,190,287.5],[17042,437,190,327],[17328,437,190,327],[17614,300,190,327],[17900,398,190,327],[18186,437,190,189.5],[6746,0,190,238.5],[18472,437,190,287.5],[18758,437,190,327],[19044,437,190,327],[19330,300,190,327],[19616,398,190,327],[19902,437,190,189.5],[7032,0,190,238.5],[20188,437,190,287.5],[20474,437,190,327],[20760,437,190,327],[21046,300,190,327],[21332,398,190,327],[21618,437,190,189.5],[7318,0,190,238.5],[21904,437,190,287.5],[22190,437,190,327],[22476,437,190,327],[286,583,190,327],[1312,747,190,327],[1598,874,190,189.5],[7604,0,190,238.5],[1884,737,190,287.5],[2170,874,190,327],[2456,874,190,327],[2742,649,190,327],[3028,747,190,327],[3314,786,190,189.5],[7890,0,190,238.5],[3600,786,190,287.5],[3886,786,190,327],[4172,786,190,327],[4458,649,190,327],[4744,747,190,327],[5030,786,190,189.5],[8176,0,190,238.5],[5316,786,190,287.5],[5602,786,190,327],[5888,786,190,327],[6174,649,190,327],[6460,747,190,327],[6746,786,190,189.5],[8462,0,190,238.5],[7032,786,190,287.5],[7318,786,190,327],[7604,786,190,327],[7890,649,190,327],[8176,747,190,327],[8462,786,190,189.5],[8748,0,190,238.5],[8748,786,190,287.5],[9034,786,190,327],[9320,786,190,327],[9606,649,190,327],[9892,747,190,327],[10178,874,190,189.5],[9034,0,190,238.5],[10464,874,190,287.5],[10750,737,190,327],[11036,835,190,327],[11322,649,190,327],[11608,835,190,327],[11894,835,190,189.5],[9320,0,190,238.5],[12180,874,190,287.5],[12466,786,190,327],[12752,874,190,327],[13038,698,190,327],[13324,835,190,327],[13610,874,190,189.5],[9606,0,190,238.5],[13896,737,190,287.5],[14182,786,190,327],[14468,835,190,327],[14754,737,190,327],[15040,835,190,327],[15326,874,190,189.5],[9892,0,190,238.5],[15612,737,190,287.5],[15898,786,190,327],[16184,835,190,327],[16470,737,190,327],[16756,835,190,327],[17042,874,190,189.5],[17328,874,190,238.5],[17614,737,190,287.5],[10178,0,190,327],[17900,835,190,327],[10464,0,190,327],[22726,2257,190,327],[10750,0,190,189.5],[18186,737,190,238.5],[11036,0,190,287.5],[18472,835,190,327],[18758,874,190,327],[19044,874,190,327],[19330,737,190,327],[19616,835,190,189.5],[11322,0,190,238.5],[22726,2656,190,287.5],[11608,0,190,327],[22726,3016,190,156],[22726,3244,190,180.5],[22726,3497,190,205],[22726,3774,190,229.5],[22726,4076,190,156],[22726,4304,190,180.5],[22726,4557,190,205],[22726,4834,190,229.5],[22726,5136,190,156],[22726,5364,190,180.5],[22726,5617,190,205],[22726,5894,190,229.5],[22726,6196,190,156],[22726,6424,190,180.5],[22726,6677,190,205],[22726,6954,190,229.5],[22726,7256,190,156],[22726,7484,190,180.5],[22726,7737,190,205],[22726,8014,190,229.5],[22726,8316,190,156],[22726,8544,190,180.5],[22726,8797,190,205],[22726,9074,190,229.5],[22726,9376,190,156],[22726,9604,190,180.5],[22726,9857,190,205],[22726,10134,190,229.5],[22726,10436,190,156],[22726,10664,190,180.5],[22726,10917,190,205],[22726,11194,190,229.5],[22726,11496,190,156],[22726,11724,190,180.5],[22726,11977,190,205],[22726,12254,190,229.5],[22726,12556,190,156],[22726,12784,190,180.5],[22726,13037,190,205],[22726,13314,190,229.5],[22726,13616,190,156],[22726,13844,190,180.5]],"lines":[[194,232.297,"right",316,177,"bottom"],[194,262.594,"right",1407,242.5,"bottom"],[194,353.5,"right",12274.999,331,"bottom"],[194,292.891,"right",2265,331,"bottom"],[194,383.797,"right",13133,291.5,"bottom"],[194,414.094,"right",13990.999,193.5,"bottom"],[194,444.391,"right",15135.001,331,"bottom"],[194,474.688,"right",16565.001,331,"bottom"],[194,323.188,"right",3091.328,242.5,"bottom"],[194,609.687,"right",346,177,"bottom"],[194,707.797,"right",12561,242.5,"bottom"],[194,642.391,"right",2551,331,"bottom"],[194,740.5,"right",13419,331,"bottom"],[194,773.188,"right",14562.999,291.5,"bottom"],[194,805.891,"right",15707.001,193.5,"bottom"],[194,838.594,"right",17137.001,331,"bottom"],[194,871.297,"right",18280.999,331,"bottom"],[194,675.094,"right",3377.328,242.5,"bottom"],[194,1767.688,"right",376,177,"bottom"],[194,1800.391,"right",2837,242.5,"bottom"],[194,1865.797,"right",13705.001,331,"bottom"],[194,1898.5,"right",14848.999,331,"bottom"],[194,1931.188,"right",16278.999,291.5,"bottom"],[194,1963.891,"right",17708.999,193.5,"bottom"],[194,1996.594,"right",18852.999,331,"bottom"],[194,2029.297,"right",19997.001,331,"bottom"],[194,1833.094,"right",3663.328,242.5,"bottom"],[194,2166.688,"right",406,177,"bottom"],[194,2232.094,"right",14277.001,242.5,"bottom"],[194,2264.797,"right",15420.999,331,"bottom"],[194,2297.5,"right",16850.999,331,"bottom"],[194,2330.188,"right",17995.001,291.5,"bottom"],[194,2362.891,"right",19425.001,193.5,"bottom"],[194,2395.594,"right",20569.001,331,"bottom"],[194,2428.297,"right",21712.999,331,"bottom"],[194,2199.391,"right",3949.328,242.5,"bottom"],[194,2940.688,"right",466,177,"bottom"],[194,3038.797,"right",15992.999,242.5,"bottom"],[194,3071.5,"right",17422.999,331,"bottom"],[194,3104.188,"right",18567.001,331,"bottom"],[194,3136.891,"right",19710.999,291.5,"bottom"],[194,3169.594,"right",21140.999,193.5,"bottom"],[194,3202.297,"right",22284.999,331,"bottom"],[194,2973.391,"right",1693,768,"bottom"],[194,3006.094,"right",4235.328,242.5,"bottom"],[194,2593.594,"right",436,177,"bottom"],[194,2654.188,"right",3154.656,242.5,"bottom"],[194,2745.094,"right",19138.999,331,"bottom"],[194,2775.391,"right",20282.999,331,"bottom"],[194,2805.688,"right",21427.001,291.5,"bottom"],[194,2563.297,"right",381,476.5,"bottom"],[194,2623.891,"right",2265,768,"bottom"],[194,2684.5,"right",3409,680,"bottom"],[194,2714.797,"right",4521.328,242.5,"bottom"],[194,3339.688,"right",496,177,"bottom"],[194,3437.797,"right",3440.656,242.5,"bottom"],[194,3568.594,"right",20854.999,331,"bottom"],[194,3601.297,"right",21999.001,331,"bottom"],[194,3372.391,"right",1407,640.5,"bottom"],[194,3405.094,"right",2837,542.5,"bottom"],[194,3470.5,"right",3981,680,"bottom"],[194,3535.891,"right",5125,680,"bottom"],[194,3503.188,"right",4807.328,242.5,"bottom"],[194,3738.687,"right",526,177,"bottom"],[194,3836.797,"right",3726.656,242.5,"bottom"],[194,4000.297,"right",22570.999,331,"bottom"],[194,3771.391,"right",1979,631,"bottom"],[194,3804.094,"right",3123,640.5,"bottom"],[194,3869.5,"right",4553,542.5,"bottom"],[194,3934.891,"right",5697,680,"bottom"],[194,3967.594,"right",6841,680,"bottom"],[194,3902.188,"right",5093.328,242.5,"bottom"],[194,4137.688,"right",556,177,"bottom"],[194,4235.797,"right",4012.656,242.5,"bottom"],[194,4170.391,"right",2551,768,"bottom"],[194,4203.094,"right",3695,680,"bottom"],[194,4268.5,"right",4839,640.5,"bottom"],[194,4333.891,"right",6269,542.5,"bottom"],[194,4366.594,"right",7413,680,"bottom"],[194,4399.297,"right",8557,680,"bottom"],[194,4301.188,"right",5379.328,242.5,"bottom"],[194,4536.687,"right",586,177,"bottom"],[194,4569.39,"right",4298.656,242.5,"bottom"],[194,4602.094,"right",4267,680,"bottom"],[194,4634.797,"right",5411,680,"bottom"],[194,4700.188,"right",6555,640.5,"bottom"],[194,4732.891,"right",7985,542.5,"bottom"],[194,4765.594,"right",9128.999,680,"bottom"],[194,4798.297,"right",10273,768,"bottom"],[194,4667.5,"right",5665.328,242.5,"bottom"],[194,4933.297,"right",616,177,"bottom"],[194,4963.594,"right",4584.656,242.5,"bottom"],[194,5024.188,"right",5983,680,"bottom"],[194,5054.5,"right",7127,680,"bottom"],[194,5084.797,"right",8271,640.5,"bottom"],[194,5115.094,"right",9701,542.5,"bottom"],[194,5145.391,"right",10844.999,631,"bottom"],[194,5175.688,"right",11989,729,"bottom"],[194,4993.891,"right",5951.328,242.5,"bottom"],[194,5310.688,"right",646,177,"bottom"],[194,5343.391,"right",4870.656,242.5,"bottom"],[194,5408.797,"right",7698.999,680,"bottom"],[194,5441.5,"right",8843,680,"bottom"],[194,5474.188,"right",9987,640.5,"bottom"],[194,5506.891,"right",11417,542.5,"bottom"],[194,5539.594,"right",12561,680,"bottom"],[194,5572.297,"right",13705.001,768,"bottom"],[194,5376.094,"right",6237.328,242.5,"bottom"],[194,5709.687,"right",676,177,"bottom"],[194,5742.39,"right",5156.657,242.5,"bottom"],[194,5807.797,"right",9415,680,"bottom"],[194,5840.5,"right",10559,768,"bottom"],[194,5873.188,"right",11703,728.5,"bottom"],[194,5905.891,"right",13133,591.5,"bottom"],[194,5938.594,"right",14277.001,680,"bottom"],[194,5971.297,"right",15420.999,768,"bottom"],[194,5775.094,"right",6523.328,242.5,"bottom"],[194,6108.687,"right",706,177,"bottom"],[194,6141.391,"right",5442.656,242.5,"bottom"],[194,6206.797,"right",11131,729,"bottom"],[194,6239.5,"right",12274.999,768,"bottom"],[194,6272.188,"right",13419,728.5,"bottom"],[194,6304.89,"right",14848.999,630.5,"bottom"],[194,6337.594,"right",15992.999,680,"bottom"],[194,6370.297,"right",17137.001,768,"bottom"],[194,6174.093,"right",6809.328,242.5,"bottom"],[194,6906.687,"right",766,177,"bottom"],[194,6939.39,"right",5728.656,242.5,"bottom"],[194,7004.797,"right",12847,768,"bottom"],[194,7037.5,"right",13990.999,631,"bottom"],[194,7070.188,"right",15135.001,728.5,"bottom"],[194,7102.89,"right",16565.001,630.5,"bottom"],[194,7135.594,"right",17708.999,631,"bottom"],[194,7168.296,"right",18852.999,768,"bottom"],[194,6972.093,"right",7095.328,242.5,"bottom"],[194,7303.296,"right",796,177,"bottom"],[194,7333.593,"right",6014.656,242.5,"bottom"],[194,7394.188,"right",14562.999,729,"bottom"],[194,7424.5,"right",15707.001,631,"bottom"],[194,7454.797,"right",16850.999,728.5,"bottom"],[194,7485.093,"right",18280.999,630.5,"bottom"],[194,7515.39,"right",19425.001,631,"bottom"],[194,7545.687,"right",20569.001,768,"bottom"],[194,7363.89,"right",7381.328,242.5,"bottom"],[194,7680.687,"right",826,177,"bottom"],[194,7713.391,"right",6300.656,242.5,"bottom"],[194,7778.797,"right",16278.999,729,"bottom"],[194,7811.5,"right",17422.999,768,"bottom"],[194,7844.188,"right",18567.001,728.5,"bottom"],[194,7876.89,"right",19997.001,630.5,"bottom"],[194,7909.594,"right",21140.999,631,"bottom"],[194,7942.297,"right",22284.999,768,"bottom"],[194,7746.093,"right",7667.328,242.5,"bottom"],[194,8112.391,"right",856,177,"bottom"],[194,8177.797,"right",6586.656,242.5,"bottom"],[194,8243.188,"right",17995.001,729,"bottom"],[194,8275.891,"right",19138.999,768,"bottom"],[194,8308.594,"right",20282.999,728.5,"bottom"],[194,8341.297,"right",21712.999,630.5,"bottom"],[194,8079.687,"right",381,914,"bottom"],[194,8145.093,"right",2265,1205,"bottom"],[194,8210.5,"right",7953.329,242.5,"bottom"],[194,8877.688,"right",916,177,"bottom"],[194,9008.5,"right",6872.656,242.5,"bottom"],[194,9073.891,"right",19710.999,729,"bottom"],[194,9106.594,"right",20854.999,768,"bottom"],[194,9139.297,"right",21999.001,728.5,"bottom"],[194,8910.391,"right",1693,1067.5,"bottom"],[194,8943.094,"right",2837,980,"bottom"],[194,8975.797,"right",3981,1117,"bottom"],[194,9041.188,"right",8239.329,242.5,"bottom"],[194,9276.687,"right",946,177,"bottom"],[194,9440.188,"right",7158.656,242.5,"bottom"],[194,9505.594,"right",21427.001,729,"bottom"],[194,9538.296,"right",22570.999,768,"bottom"],[194,9309.39,"right",1979,1028.5,"bottom"],[194,9342.093,"right",3409,979.5,"bottom"],[194,9374.797,"right",4553,980,"bottom"],[194,9407.5,"right",5697,1117,"bottom"],[194,9472.89,"right",8525.328,242.5,"bottom"],[194,9673.296,"right",976,177,"bottom"],[194,9855.093,"right",7444.656,242.5,"bottom"],[194,9703.593,"right",1407,1078,"bottom"],[194,9733.89,"right",2551,1205,"bottom"],[194,9764.188,"right",3695,1077.5,"bottom"],[194,9794.5,"right",5125,979.5,"bottom"],[194,9824.797,"right",6269,980,"bottom"],[194,9885.39,"right",7413,1117,"bottom"],[194,9915.687,"right",8811.328,242.5,"bottom"],[194,10050.687,"right",1006,177,"bottom"],[194,10214.188,"right",7730.656,242.5,"bottom"],[194,10083.391,"right",3123,1078,"bottom"],[194,10116.093,"right",4267,1117,"bottom"],[194,10148.797,"right",5411,1077.5,"bottom"],[194,10181.5,"right",6841,979.5,"bottom"],[194,10246.891,"right",7985,980,"bottom"],[194,10312.297,"right",9128.999,1117,"bottom"],[194,10279.594,"right",9097.328,242.5,"bottom"],[194,10449.687,"right",1036,177,"bottom"],[194,10580.5,"right",8016.656,242.5,"bottom"],[194,10482.391,"right",4839,1078,"bottom"],[194,10515.094,"right",5983,1117,"bottom"],[194,10547.797,"right",7127,1077.5,"bottom"],[194,10613.188,"right",8557,979.5,"bottom"],[194,10678.594,"right",9701,980,"bottom"],[194,10711.297,"right",10844.999,1068,"bottom"],[194,10645.891,"right",9383.329,242.5,"bottom"],[194,11247.687,"right",1096,177,"bottom"],[194,11345.797,"right",8302.656,242.5,"bottom"],[194,11280.39,"right",6555,1078,"bottom"],[194,11313.093,"right",7698.999,1117,"bottom"],[194,11378.5,"right",8843,1077.5,"bottom"],[194,11443.89,"right",10273,1067.5,"bottom"],[194,11476.594,"right",11417,980,"bottom"],[194,11509.296,"right",12561,1117,"bottom"],[194,11411.188,"right",9732.656,242.5,"bottom"],[194,11646.687,"right",1126,177,"bottom"],[194,11679.391,"right",8588.656,242.5,"bottom"],[194,11712.093,"right",8271,1078,"bottom"],[194,11744.797,"right",9415,1117,"bottom"],[194,11810.188,"right",10559,1165.5,"bottom"],[194,11842.89,"right",11989,1028.5,"bottom"],[194,11875.594,"right",13133,1029,"bottom"],[194,11908.297,"right",14277.001,1117,"bottom"],[194,11777.5,"right",10018.656,242.5,"bottom"],[194,12442.297,"right",1186,177,"bottom"],[194,12472.594,"right",8874.656,242.5,"bottom"],[194,12502.89,"right",9987,1078,"bottom"],[194,12533.188,"right",11131,1166,"bottom"],[194,12563.5,"right",12274.999,1165.5,"bottom"],[194,12593.797,"right",13705.001,1067.5,"bottom"],[194,12624.094,"right",14848.999,1068,"bottom"],[194,12654.391,"right",15992.999,1117,"bottom"],[194,12684.687,"right",17422.999,1116.5,"bottom"],[194,12045.687,"right",1156,177,"bottom"],[194,12078.391,"right",9160.656,242.5,"bottom"],[194,12143.797,"right",11703,1166,"bottom"],[194,12176.5,"right",12847,1205,"bottom"],[194,12209.188,"right",13990.999,1028.5,"bottom"],[194,12241.891,"right",15420.999,1067.5,"bottom"],[194,12274.594,"right",16565.001,1068,"bottom"],[194,12111.093,"right",10304.656,331,"bottom"],[194,12307.297,"right",18280.999,979.5,"bottom"],[194,10848.688,"right",1066,177,"bottom"],[194,10881.391,"right",9446.656,242.5,"bottom"],[194,10979.5,"right",13419,1166,"bottom"],[194,11012.188,"right",14562.999,1166,"bottom"],[194,11044.891,"right",15707.001,1028.5,"bottom"],[194,11077.594,"right",17137.001,1067.5,"bottom"],[194,10914.094,"right",10590.656,331,"bottom"],[194,11110.297,"right",18567.001,1166,"bottom"],[194,10946.797,"right",11448.656,242.5,"bottom"],[194,8478.687,"right",886,177,"bottom"],[194,8544.094,"right",9669.329,242.5,"bottom"],[194,8642.188,"right",15135.001,1166,"bottom"],[194,8674.891,"right",16278.999,1166,"bottom"],[194,8707.594,"right",17708.999,1028.5,"bottom"],[194,8576.797,"right",10876.656,193.5,"bottom"],[194,8740.297,"right",19138.999,1205,"bottom"],[194,8609.5,"right",11734.656,331,"bottom"],[194,8511.391,"right",1693,331,"bottom"],[194,6507.687,"right",736,177,"bottom"],[194,6573.093,"right",9955.328,242.5,"bottom"],[194,6703.891,"right",16850.999,1166,"bottom"],[194,6736.594,"right",17995.001,1166,"bottom"],[194,6605.797,"right",11162.656,291.5,"bottom"],[194,6769.297,"right",19710.999,1028.5,"bottom"],[194,6638.5,"right",11989,291.5,"bottom"],[194,6540.391,"right",1979,193.5,"bottom"],[194,6671.188,"right",12847,331,"bottom"],[1506,119.25,"right",11671.328,331,"bottom"],[194,1499.5,"right",11385.329,242.5,"bottom"],[1792,163.5,"right",19425.001,1068,"bottom"],[2078,94.75,"right",18852.999,1205,"bottom"],[194,1119.75,"right",11099.329,291.5,"bottom"],[2364,163.5,"right",10813.328,193.5,"bottom"],[2650,163.5,"right",10527.328,331,"bottom"],[2936,119.25,"right",10241.328,331,"bottom"]]};
for (const [name, fix, room] of [["80 tables in the assistant's plain grid, 40 px apart, room made", GRID80, true], ["300 tables in the Studio's own bus matrix, every card open", OPEN300, false]]) {
  ok(`${name}: every line round the cards, none on top of another (the live page's geometry)`, () => {
    const { boxes, lines } = room ? madeRoom(unpack(fix)) : unpack(fix);
    const r = routeAll(lines, boxes);
    checkAll(r, lines, boxes);
    assert.deepEqual(onTop(r, lines), [], 'lines drawn on top of each other');
  });
}

// A guard, not a bug proof (the round 2 router passes it too): a spread run keeps clear of every card at the margin
// its route records, so a drag may keep it; and the extra passes stay bounded on a grid packed past any fix.
ok('spread routes keep the clearance they record, and a packed grid (80 tables 20 and 10 px apart) stays bounded', () => {
  for (const gap of [40, 20, 10]) {
    const { boxes, lines } = assistantGrid(8, 60, 12, 9, gap);
    const t0 = performance.now();
    const r = routeAll(lines, boxes);
    const ms = performance.now() - t0;
    checkAll(r, lines, boxes);
    assert.ok(ms < 4000, `${gap} px: one pass took ${Math.round(ms)} ms (budget 4000 ms)`);
    for (const l of lines) {
      const { pts, margin } = r.get(l.id);
      if (margin < 0) continue;
      const m = Math.max(0, margin);
      const grown = boxes.map((b) => ({ id: b.id, x: b.x - m, y: b.y - m, w: b.w + 2 * m, h: b.h + 2 * m }));
      const own = (p) => boxes.findIndex((b) => (p.side === 'right' ? Math.abs(p.x - b.x - b.w) <= 12 && p.y >= b.y && p.y <= b.y + b.h : Math.abs(p.y - b.y - b.h) <= 12 && p.x >= b.x && p.x <= b.x + b.w));
      const a = own(l.src), z = own(l.dst);
      for (let i = 1; i < pts.length; i++) {
        const hit = through([pts[i - 1], pts[i]], grown).filter((id) => !(i === 1 && id === boxes[a].id) && !(i === pts.length - 1 && id === boxes[z].id));
        assert.deepEqual(hit, [], `${gap} px: ${l.id} comes closer than its recorded ${margin} px to ${hit.join(', ')}: ${pathOf(pts)}`);
      }
    }
  }
  assert.ok(MARGINS.includes(0));
});

// ---- 4: holdRoutes ----
ok('holdRoutes keeps an unmoved route, holds a moved one on its new ends, and holds nothing never routed', () => {
  assert.equal(typeof R.holdRoutes, 'function', 'holdRoutes is exported');
  const { boxes, lines } = assistantGrid(4, 20, 6, 7, 40);
  const first = routeAll(lines, boxes);
  const dx = 37, dy = 23;
  const moved = lines.map((l, i) => (i % 2 ? { ...l, src: { ...l.src, x: l.src.x + dx, y: l.src.y + dy } } : l));
  const held = R.holdRoutes(first, [...moved, { id: 'new', src: { x: 0, y: 0, side: 'right' }, dst: { x: 9, y: 9, side: 'bottom' } }]);
  assert.equal(held.has('new'), false, 'a line never routed has nothing to hold');
  moved.forEach((l, i) => {
    const h = held.get(l.id);
    if (!(i % 2)) { assert.equal(h, first.get(l.id), `${l.id}: an unmoved line keeps its very route`); return; }
    assert.equal(h.held, true, `${l.id} is marked held`);
    assert.equal(h.margin, -1, `${l.id}: a held route is never kept by a drag`);
    const p = h.pts, q = first.get(l.id).pts;
    assert.deepEqual([p[0].x, p[0].y, p.at(-1).x, p.at(-1).y], [l.src.x, l.src.y, l.dst.x, l.dst.y], `${l.id} starts and ends on its new handles`);
    assert.ok(p.every((c, k) => !k || c.x === p[k - 1].x || c.y === p[k - 1].y), `${l.id} stays right-angled: ${pathOf(p)}`);
    assert.equal(p[0].y, p[1].y, `${l.id} leaves its right-side handle sideways`);
    assert.equal(p.at(-1).x, p.at(-2).x, `${l.id} enters its bottom handle upward`);
    if (q.length > 4 && p.length === q.length) assert.deepEqual(p.slice(2, -2), q.slice(2, -2), `${l.id} keeps its inner corners`);
    assert.deepEqual(h.src, l.src, 'the held record carries the new ends, so the edge draws it');
  });
  // A drag frame routes a held line again rather than keep it.
  const again = routeAll(moved, boxes, held);
  moved.forEach((l, i) => { if (i % 2) assert.notEqual(again.get(l.id), held.get(l.id), `${l.id} is routed again`); });
});

// ---- 5: clearDropped ----
ok('a card dropped over others slides the shortest way clear, and only the dropped cards move', () => {
  assert.equal(typeof F.clearDropped, 'function', 'clearDropped is exported');
  const hits = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
  const date = { id: 'Date', x: 286, y: 0, w: 190, h: 92 }, store = { id: 'Store', x: 286, y: 202, w: 190, h: 92 }, sales = { id: 'Sales', x: 0, y: 202, w: 190, h: 300 };
  // Dropped half over Date: it slides down, the shorter way, and Date stays.
  const promo = { id: 'Promotion', x: 300, y: 46, w: 190, h: 92 };
  let out = F.clearDropped([date, store, sales, promo], ['Promotion']);
  assert.deepEqual(Object.keys(out), ['Promotion']);
  let moved = { ...promo, ...out.Promotion };
  assert.ok(![date, store, sales].some((c) => hits(moved, c)), `Promotion still covers a card at ${JSON.stringify(out.Promotion)}`);
  // Its first way out (straight down, just clear of Date) lands on Store: it takes the next way that is clear.
  const onto = { ...promo, y: 150 };
  out = F.clearDropped([date, store, sales, onto], ['Promotion']);
  moved = { ...onto, ...out.Promotion };
  assert.ok(![date, store, sales].some((c) => hits(moved, c)), `Promotion still covers a card at ${JSON.stringify(out.Promotion)}`);
  // Dropped clear of everything: nothing moves.
  assert.deepEqual(F.clearDropped([date, store, sales, { ...promo, x: 700 }], ['Promotion']), {});
  // Two cards dropped together clear each other as well as the rest.
  out = F.clearDropped([date, store, sales, promo, { ...promo, id: 'Currency', x: 320 }], ['Promotion', 'Currency']);
  const a = { ...promo, ...out.Promotion }, b = { ...promo, id: 'Currency', x: 320, ...out.Currency };
  assert.ok(!hits(a, b) && ![date, store, sales].some((c) => hits(a, c) || hits(b, c)), 'the two dropped cards clear each other and the rest');
});

console.log(`diagram-route-r3: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
