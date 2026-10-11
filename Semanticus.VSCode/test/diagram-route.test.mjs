// Diagram lines never run behind a card (the space rule, 27 Sep): a line finds its way through the gaps between cards,
// wherever a drag or the assistant leaves them. The bus matrix itself keeps its lookup tables in one row (Kane, 8 Oct). Executes the SAME pure module the webview ships
// (webview/src/diagramroute.mjs); the crossing check is the test's own, so a router that returns nothing cannot pass.
// The live proof on the rendered canvas is tools/uishot/diagram-route-drive.mjs.
import assert from 'node:assert/strict';
import { routeOrthogonal, routeAll, busGrid, busOrder, segmentHitsBox, pathOf, sharedLength, trackFraction, MARGIN, OPEN, NEAR } from '../webview/src/diagramroute.mjs';

let passed = 0;
const ok = (name, fn) => { fn(); passed++; console.log('  [PASS] ' + name); };

// The test's own crossing check: every segment against every card, strict inside.
const crosses = (pts, boxes) => {
  const out = [];
  for (let i = 1; i < pts.length; i++) for (const b of boxes) {
    const a = pts[i - 1], c = pts[i];
    const x0 = Math.min(a.x, c.x), x1 = Math.max(a.x, c.x), y0 = Math.min(a.y, c.y), y1 = Math.max(a.y, c.y);
    if (x0 < b.x + b.w && x1 > b.x && y0 < b.y + b.h && y1 > b.y) out.push(b.id);
  }
  return out;
};
const orthogonal = (pts) => pts.every((p, i) => !i || p.x === pts[i - 1].x || p.y === pts[i - 1].y);

ok('a line with a clear path is a simple L and ends on both handles', () => {
  const fact = { id: 'f', x: 0, y: 200, w: 190, h: 92 }, dim = { id: 'd', x: 300, y: 0, w: 190, h: 92 };
  const src = { x: 190, y: 246, side: 'right' }, dst = { x: 395, y: 92, side: 'bottom' };
  const pts = routeOrthogonal(src, dst, [fact, dim]);
  assert.ok(pts, 'a route was found');
  assert.deepEqual(pts[0], { x: 190, y: 246 });
  assert.deepEqual(pts.at(-1), { x: 395, y: 92 });
  assert.ok(orthogonal(pts), 'every segment is horizontal or vertical');
  assert.equal(pts.length, 3, 'one turn: ' + pathOf(pts));
});

ok('a dimension in the second row routes round the first row, never behind it', () => {
  // Fact on the left; row one holds A and B; the target C sits under A in row two.
  const fact = { id: 'f', x: 0, y: 160, w: 190, h: 300 };
  const A = { id: 'A', x: 300, y: 0, w: 190, h: 92 }, B = { id: 'B', x: 580, y: 0, w: 190, h: 92 };
  const C = { id: 'C', x: 300, y: 200, w: 190, h: 92 }, D = { id: 'D', x: 580, y: 200, w: 190, h: 92 };
  const boxes = [fact, A, B, C, D];
  // Fact's right edge high up joins D's bottom: the straight L would cut through C.
  const pts = routeOrthogonal({ x: 190, y: 180, side: 'right' }, { x: 675, y: 292, side: 'bottom' }, boxes);
  assert.ok(pts);
  assert.ok(orthogonal(pts));
  assert.deepEqual(crosses(pts, boxes), [], 'crosses a card: ' + pathOf(pts));
});

ok('a boxed-in target has no route rather than a line through a card', () => {
  const t = { id: 't', x: 100, y: 100, w: 50, h: 50 };
  const walls = [{ id: 'n', x: 60, y: 30, w: 130, h: 60 }, { id: 's', x: 60, y: 160, w: 130, h: 60 },
    { id: 'w', x: 20, y: 30, w: 70, h: 190 }, { id: 'e', x: 160, y: 30, w: 70, h: 190 }];
  assert.equal(routeOrthogonal({ x: 500, y: 500, side: 'right' }, { x: 125, y: 150, side: 'bottom' }, [t, ...walls]), null);
});

ok('two lines on different tracks both route clear of the cards', () => {
  const fact = { id: 'f', x: 0, y: 200, w: 190, h: 200 }, A = { id: 'A', x: 300, y: 0, w: 190, h: 92 };
  const boxes = [fact, A];
  const p0 = routeOrthogonal({ x: 190, y: 250, side: 'right' }, { x: 360, y: 92, side: 'bottom' }, boxes, 0);
  const p1 = routeOrthogonal({ x: 190, y: 300, side: 'right' }, { x: 430, y: 92, side: 'bottom' }, boxes, 1);
  assert.ok(p0 && p1);
  assert.deepEqual(crosses(p0, boxes), []); assert.deepEqual(crosses(p1, boxes), []);
});

ok('segmentHitsBox: touching a border is not a hit, passing through is', () => {
  const r = { x: 0, y: 0, w: 10, h: 10 };
  assert.equal(segmentHitsBox({ x: 10, y: -5 }, { x: 10, y: 15 }, r), false);
  assert.equal(segmentHitsBox({ x: 5, y: -5 }, { x: 5, y: 15 }, r), true);
});

ok('a dense 30-card grid routes every line clear of every card', () => {
  const boxes = [];
  const facts = [0, 1, 2, 3].map((i) => ({ id: 'F' + i, x: 0, y: 202 + i * 240, w: 190, h: 180 }));
  const dims = Array.from({ length: 24 }, (_, i) => ({ id: 'D' + i, x: 286 + (i % 6) * 286, y: Math.floor(i / 6) * 202, w: 190, h: 92 }));
  boxes.push(...facts, ...dims);
  let n = 0;
  for (const [fi, f] of facts.entries()) for (let k = 0; k < 6; k++) {
    const d = dims[(fi * 5 + k * 3) % dims.length];
    const pts = routeOrthogonal({ x: f.x + f.w, y: f.y + 20 + k * 25, side: 'right' }, { x: d.x + 40 + fi * 30, y: d.y + d.h, side: 'bottom' }, boxes, n++);
    assert.ok(pts, `route F${fi} -> ${d.id}`);
    assert.ok(orthogonal(pts));
    assert.deepEqual(crosses(pts, boxes), [], `F${fi} -> ${d.id}: ${pathOf(pts)}`);
  }
});

// ---- Pins for what the router promises (m11/diagram-route verification D5: MARGIN = 0 and one-track mutants
// survived every test before these) ----

// The inner corners of a route (its stubs excepted) and their distance to a box, in flow px.
const gapTo = (a, b, r) => {
  const dx = Math.max(r.x - Math.max(a.x, b.x), Math.min(a.x, b.x) - (r.x + r.w), 0);
  const dy = Math.max(r.y - Math.max(a.y, b.y), Math.min(a.y, b.y) - (r.y + r.h), 0);
  return Math.max(dx, dy);
};
const clearance = (pts, boxes) => { let m = Infinity; for (let i = 2; i < pts.length - 1; i++) for (const b of boxes) m = Math.min(m, gapTo(pts[i - 1], pts[i], b)); return m; };

ok('a line keeps MARGIN px from every card: it goes round rather than squeeze through a gap narrower than 2 x MARGIN', () => {
  // A wall of two cards with an 18 px slit, the fact on its left and the target on its right; the way round is
  // over the top. A router with no margin (or a smaller one) threads the slit.
  const fact = { id: 'F', x: 0, y: 200, w: 190, h: 100 }, U = { id: 'U', x: 400, y: 0, w: 100, h: 240 }, Lo = { id: 'Lo', x: 400, y: 258, w: 100, h: 342 };
  const T = { id: 'T', x: 700, y: 200, w: 190, h: 100 };
  const boxes = [fact, U, Lo, T];
  const pts = routeOrthogonal({ x: 194, y: 250, side: 'right' }, { x: 696, y: 250, side: 'left' }, boxes);
  assert.ok(pts, 'routed');
  assert.deepEqual(crosses(pts, boxes), []);
  assert.ok(clearance(pts, boxes) >= MARGIN, `comes within ${clearance(pts, boxes)} px of a card: ${pathOf(pts)}`);
  assert.ok(!pts.some((p, i) => i && p.y === pts[i - 1].y && p.y > 240 && p.y < 258 && Math.min(p.x, pts[i - 1].x) < 500 && Math.max(p.x, pts[i - 1].x) > 400), 'threads the slit: ' + pathOf(pts));
});

ok('a line through a gap between two cards runs down its middle, whatever the gap', () => {
  for (const gap of [96, 40, 30]) {   // narrower than 2 x MARGIN + MIN_FREE, a line goes round (the test above)
    // L and R side by side with the gap between them, a card under both and one over both. The line joins the
    // lower card's top (under L) to the upper card's bottom (over R), so it must climb through the gap.
    const L = { id: 'L', x: 0, y: 0, w: 190, h: 400 }, R = { id: 'R', x: 190 + gap, y: 0, w: 190, h: 400 };
    const low = { id: 'Low', x: 0, y: 520, w: 380 + gap, h: 92 }, high = { id: 'High', x: 0, y: -200, w: 380 + gap, h: 92 };
    const boxes = [L, R, low, high];
    const pts = routeOrthogonal({ x: 100, y: 516, side: 'top' }, { x: 300 + gap, y: -104, side: 'bottom' }, boxes);
    assert.ok(pts, `routed through a ${gap} px gap`);
    assert.deepEqual(crosses(pts, boxes), [], pathOf(pts));
    const run = pts.find((p, i) => i && p.x === pts[i - 1].x && Math.min(p.y, pts[i - 1].y) < 400 && Math.max(p.y, pts[i - 1].y) > 0);
    assert.ok(run, 'a climb inside the gap: ' + pathOf(pts));
    assert.equal(run.x, 190 + gap / 2, `a ${gap} px gap: the line climbs at ${run.x}, not its middle ${190 + gap / 2}: ${pathOf(pts)}`);
  }
});

ok('tracks sit at different places across a gap: 1/2, 1/4, 3/4, 1/8 and on, never twice', () => {
  const f = Array.from({ length: 64 }, (_, t) => trackFraction(t));
  assert.deepEqual(f.slice(0, 4), [0.5, 0.25, 0.75, 0.125]);
  assert.equal(new Set(f).size, 64);
  assert.ok(f.every((v) => v > 0 && v < 1));
});

// Six lines leave one fact for six lookup tables in a row above a wide card, so all six must run along the one
// channel between that row and the wide card: a router with a single track draws them on top of each other.
function channelScene() {
  const fact = { id: 'F', x: 0, y: 404, w: 190, h: 210 }, wall = { id: 'W', x: 286, y: 150, w: 714, h: 550 };
  const dims = Array.from({ length: 6 }, (_, i) => ({ id: 'D' + i, x: 300 + i * 120, y: 0, w: 100, h: 92 }));
  const lines = dims.map((d, i) => ({ id: 'F>' + d.id, src: { x: 194, y: 404 + 30 * (i + 1), side: 'right' }, dst: { x: d.x + 50, y: d.y + 96, side: 'bottom' } }));
  return { boxes: [fact, wall, ...dims], lines };
}
ok('routeAll gives lines that share a channel their own tracks: none runs along another', () => {
  const { boxes, lines } = channelScene();
  const r = routeAll(lines, boxes);
  for (const l of lines) { assert.ok(r.get(l.id).pts, l.id + ' routed'); assert.deepEqual(crosses(r.get(l.id).pts, boxes), [], l.id); }
  for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {
    const a = r.get(lines[i].id).pts, b = r.get(lines[j].id).pts;
    assert.equal(sharedLength(a, b), 0, `${lines[i].id} runs along ${lines[j].id}: ${pathOf(a)} | ${pathOf(b)}`);
  }
  assert.ok(new Set([...r.values()].map((v) => v.track)).size > 1, 'more than one track handed out');
});

ok('routeAll keeps the routes a drag did not disturb and routes the rest', () => {
  const { boxes, lines } = channelScene();
  const first = routeAll(lines, boxes);
  const moved = boxes.map((b) => (b.id === 'D5' ? { ...b, x: b.x + 40 } : b));
  const next = routeAll(lines.map((l) => (l.id === 'F>D5' ? { ...l, dst: { ...l.dst, x: l.dst.x + 40 } } : l)), moved, first);
  assert.equal(next.get('F>D0'), first.get('F>D0'), 'an untouched line keeps its very route');
  assert.notEqual(next.get('F>D5'), first.get('F>D5'), 'the moved card\'s line is routed again');
  assert.deepEqual(crosses(next.get('F>D5').pts, moved), []);
});

// The verification's D2: cards closer than the router's margin allowed made it give up, and the Diagram then drew
// the old step line straight through a card.
ok('a card dragged close under another (36, 20 and 4 px): every line still routes round the cards', () => {
  for (const gap of [36, 20, 4]) {
    const date = { id: 'Date', x: 286, y: 0, w: 190, h: 92 }, promo = { id: 'Promotion', x: 286, y: 92 + gap, w: 190, h: 92 };
    const fact = { id: 'Sales', x: 0, y: 202, w: 190, h: 150 }, prod = { id: 'Product', x: 572, y: 0, w: 190, h: 92 };
    const boxes = [date, promo, fact, prod];
    const lines = [
      { id: 'Sales>Date', src: { x: 194, y: 240, side: 'right' }, dst: { x: 350, y: 96, side: 'bottom' } },
      { id: 'Sales>Date2', src: { x: 194, y: 270, side: 'right' }, dst: { x: 410, y: 96, side: 'bottom' } },
      { id: 'Sales>Promotion', src: { x: 194, y: 300, side: 'right' }, dst: { x: 381, y: promo.y + 96, side: 'bottom' } },
      { id: 'Sales>Product', src: { x: 194, y: 330, side: 'right' }, dst: { x: 667, y: 96, side: 'bottom' } },
    ];
    const r = routeAll(lines, boxes);
    for (const l of lines) {
      const pts = r.get(l.id).pts;
      assert.ok(pts, `${gap} px: ${l.id} has a route`);
      assert.deepEqual(crosses(pts, boxes), [], `${gap} px: ${l.id} ${pathOf(pts)}`);
    }
  }
});

ok("a tidy grid the person's assistant saves (40 and 20 px gaps): every line routes round the cards, none along another", () => {
  // 20.0001: measured positions carry float noise, which leaves a 20 px gap a hair wider than two full margins.
  for (const gap of [40, 20, 20.0001]) {
    const dims = Array.from({ length: 8 }, (_, i) => ({ id: 'D' + i, x: 286 + (i % 4) * (190 + gap), y: Math.floor(i / 4) * (92 + gap), w: 190, h: 92 }));
    const facts = [0, 1].map((i) => ({ id: 'F' + i, x: 0, y: 2 * (92 + gap) + 60 + i * 260, w: 190, h: 220 }));
    const boxes = [...dims, ...facts];
    const lines = [];
    facts.forEach((f, fi) => dims.forEach((d, di) => { if ((di + fi) % 2 === 0 || di < 2) lines.push({ id: `${f.id}>${d.id}`, src: { x: 194, y: f.y + 20 * (lines.length % 10 + 1), side: 'right' }, dst: { x: d.x + 30 + 30 * fi + 10 * (di % 2), y: d.y + 96, side: 'bottom' } }); }));
    const r = routeAll(lines, boxes);
    for (const l of lines) {
      const pts = r.get(l.id).pts;
      assert.ok(pts, `${gap} px grid: ${l.id} has a route`);
      assert.deepEqual(crosses(pts, boxes), [], `${gap} px grid: ${l.id} ${pathOf(pts)}`);
    }
    // A gap too narrow for separate tracks at the full margin must not stack every track on its one middle line.
    for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {
      const a = r.get(lines[i].id).pts, b = r.get(lines[j].id).pts;
      assert.ok(sharedLength(a, b) <= 8, `${gap} px grid: ${lines[i].id} runs ${sharedLength(a, b)} px along ${lines[j].id}: ${pathOf(a)} | ${pathOf(b)}`);
    }
  }
});

// The live page's own geometry for a 20 px grid the assistant saved (tools/uishot/diagram-route-scale-drive.mjs):
// handle positions carry measurement noise, and at the full margin a 20 px slit has no width left, so every track
// fell on its one middle line and two pairs of lines were drawn on top of each other.
const GRID20 = {"boxes":[[0,264,190,270],[300,0,190,92],[510,0,190,92],[720,0,190,92],[930,0,190,92],[300,112,190,92],[510,112,190,92],[720,112,190,92],[930,112,190,92],[1240,0,190,92],[1240,112,190,92]],"lines":[[194.00003693301048,294.0001162369429,"right",395.0000173594867,96.00003220388658,"bottom"],[194.00003693301048,444.0001332436203,"right",573.3282557845181,208.00009963166536,"bottom"],[194.00003693301048,354.0001494474981,"right",815.0001946831472,96.00003220388658,"bottom"],[194.00003693301048,474.0001498488979,"right",1056.6566189498312,208.00009963166536,"bottom"],[194.00003693301048,414.0001826580532,"right",395.0000503693419,208.00009963166536,"bottom"],[194.00003693301048,324.00013284222047,"right",573.3282557845181,96.00003220388658,"bottom"],[194.00003693301048,504.0001664541754,"right",815.0002276930024,208.00009963166536,"bottom"],[194.00003693301048,384.0001660527756,"right",993.3284661180339,96.00004870881418,"bottom"],[494.0000699428657,46.00000674246099,"right",993.3284661180339,208.00009963166536,"bottom"],[914.0003132862366,46.00000674246099,"right",636.6564086163154,208.00009963166536,"bottom"],[494.0000369330105,158.00007417023977,"right",1056.6565859399761,96.00004870881418,"bottom"],[914.0002802763814,158.00007417023977,"right",636.6564086163154,96.00003220388658,"bottom"]]};
ok('a 20 px slit is too narrow for separate tracks at the full margin: lines take a smaller one, or another channel', () => {
  const boxes = GRID20.boxes.map(([x, y, w, h]) => ({ x, y, w, h }));
  const lines = GRID20.lines.map(([sx, sy, ss, tx, ty, ts], i) => ({ id: 'L' + i, src: { x: sx, y: sy, side: ss }, dst: { x: tx, y: ty, side: ts } }));
  const r = routeAll(lines, boxes);
  for (const l of lines) { assert.ok(r.get(l.id).pts, l.id + ' routed'); assert.deepEqual(crosses(r.get(l.id).pts, boxes), [], l.id + ' ' + pathOf(r.get(l.id).pts)); }
  for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {
    const a = r.get(lines[i].id).pts, b = r.get(lines[j].id).pts;
    assert.ok(sharedLength(a, b) <= 8, `L${i} runs ${Math.round(sharedLength(a, b))} px along L${j}: ${pathOf(a)} | ${pathOf(b)}`);
  }
});

ok('cards stacked on a handle: the line passes under only the card that covers it, round every other', () => {
  const up = { id: 'Up', x: 286, y: 0, w: 190, h: 92 }, cover = { id: 'Cover', x: 286, y: 92, w: 190, h: 92 };
  const fact = { id: 'F', x: 0, y: 260, w: 190, h: 100 }, other = { id: 'Other', x: 120, y: 120, w: 60, h: 60 };
  const pts = routeOrthogonal({ x: 194, y: 300, side: 'right' }, { x: 380, y: 96, side: 'bottom' }, [up, cover, fact, other]);
  assert.ok(pts, 'a route');
  assert.deepEqual(crosses(pts, [up, fact, other]), [], pathOf(pts));
});

// ---- One row (Kane, 8 Oct: the wrapped rows were the broken bus matrix) ----
const cards = (n, w = 190, h = 92) => Array.from({ length: n }, (_, i) => ({ w: w + (i % 3) * 30, h: h + (i % 4) * 20 }));
const overlaps = (g, dims, facts, iso) => {
  const all = [...g.dims.map((p, i) => ({ ...p, ...dims[i] })), ...g.facts.map((p, i) => ({ ...p, ...facts[i] })), ...g.iso.map((p, i) => ({ ...p, ...iso[i] }))];
  const out = [];
  for (let i = 0; i < all.length; i++) for (let j = i + 1; j < all.length; j++) {
    const a = all[i], b = all[j];
    if (a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h) out.push(`${i}/${j}`);
  }
  return out;
};
ok('the lookup tables sit in one row, outriggers beside their table, facts under the row, fewest crossings', () => {
  // AdventureWorks' links: Product Category hangs off Product Subcategory, which hangs off Product; Geography off Customer and Reseller.
  const L = (many, one) => ({ many, one });
  const names = ['Currency', 'Customer', 'Date', 'Employee', 'Geography', 'Product', 'Product Category', 'Product Subcategory', 'Promotion', 'Reseller', 'Sales Territory', 'Internet Sales', 'Product Inventory', 'Reseller Sales', 'Sales Quota', 'Notes'];
  const links = [L('Customer', 'Geography'), L('Employee', 'Sales Territory'), L('Geography', 'Sales Territory'), L('Product', 'Product Subcategory'), L('Product Subcategory', 'Product Category'), L('Reseller', 'Geography'),
    ...['Currency', 'Customer', 'Date', 'Date', 'Product', 'Promotion', 'Sales Territory'].map((d) => L('Internet Sales', d)), L('Product Inventory', 'Date'), L('Product Inventory', 'Product'),
    ...['Currency', 'Date', 'Employee', 'Product', 'Promotion', 'Reseller', 'Sales Territory'].map((d) => L('Reseller Sales', d)), L('Sales Quota', 'Date'), L('Sales Quota', 'Employee')];
  const o = busOrder(names, links);
  assert.deepEqual([...o.facts].sort(), ['Internet Sales', 'Product Inventory', 'Reseller Sales', 'Sales Quota']);
  assert.deepEqual(o.iso, ['Notes']);
  const at = (n) => o.dims.indexOf(n);
  assert.equal(at('Product Subcategory'), at('Product') + 1, 'Product Subcategory sits right after Product');
  assert.equal(at('Product Category'), at('Product Subcategory') + 1, 'Product Category right after Product Subcategory');
  assert.ok([at('Customer'), at('Reseller')].includes(at('Geography') - 1), 'Geography right after one of its two tables');
  const sz = (n) => ({ w: 190 + n.length, h: 92 + (n.length % 3) * 30 });
  const g = busGrid(o.dims.map(sz), o.facts.map(sz), o.iso.map(sz), 1.6);
  assert.equal(new Set(g.dims.map((p) => p.y)).size, 1, 'one row, at any canvas shape');
  assert.ok(g.dims.every((p, i) => !i || p.x > g.dims[i - 1].x), 'left to right in order');
  const rowBottom = Math.max(...o.dims.map((n) => sz(n).h));
  assert.ok(g.facts.every((p) => p.x === 0 && p.y > rowBottom), 'facts down the left, under the row');
  assert.deepEqual(overlaps(g, o.dims.map(sz), o.facts.map(sz), o.iso.map(sz)), []);
  // Fewest crossings: a fact's lookup tables go left of the next fact's (a line from further right to a higher fact crosses).
  const s = busOrder(['F0', 'F1', 'C', 'A', 'B'], [L('F1', 'C'), L('F0', 'A'), L('F0', 'B')]);
  assert.deepEqual([s.dims, s.facts], [['A', 'B', 'C'], ['F0', 'F1']]);
});

ok('unlinked tables carry on the row to the right, never level with a fact: a lone fact\'s lines stay plain Ls', () => {
  // A card level with a fact made the channel off the fact's right side as wide as the row, so the fact's lines ran
  // halfway across the diagram and back (the Contoso mock's Sales -> Date, 8 Oct).
  for (const [nd, nf, ni] of [[8, 1, 2], [32, 8, 40]]) {
    const dims = cards(nd), facts = cards(nf, 190, 240), iso = cards(ni);
    const g = busGrid(dims, facts, iso, 1.8);
    const factTop = Math.min(...g.facts.map((p) => p.y)), rowRight = Math.max(...g.dims.map((p, i) => p.x + dims[i].w));
    assert.ok(g.iso.every((p, i) => p.y + iso[i].h < factTop), `${ni} unlinked: none level with a fact`);
    assert.ok(g.iso.every((p) => p.x > rowRight), `${ni} unlinked: right of the row`);
    assert.deepEqual(overlaps(g, dims, facts, iso), []);
  }
  // Routed, the Contoso mock's lone fact (two unlinked tables) gets plain Ls: one turn each.
  const box = (id, x, y, w = 190, h = 92) => ({ id, x, y, w, h });
  const names = ['Date', 'Product', 'Currency', 'Customer', 'Geography', 'Store'];
  const g = busGrid(names.map(() => ({ w: 190, h: 92 })), [{ w: 190, h: 210 }], [{ w: 190, h: 92 }, { w: 190, h: 92 }], 1.6);
  const boxes = [box('Sales', g.facts[0].x, g.facts[0].y, 190, 210), ...names.map((n, i) => box(n, g.dims[i].x, g.dims[i].y)), ...g.iso.map((p, i) => box('L' + i, p.x, p.y))];
  const lines = ['Date', 'Product', 'Customer', 'Store'].map((n, i) => { const d = boxes.find((b) => b.id === n); return { id: n, src: { x: 194, y: g.facts[0].y + 30 * (i + 1), side: 'right' }, dst: { x: d.x + 95, y: 96, side: 'bottom' } }; });
  for (const [id, r] of routeAll(lines, boxes)) assert.equal(r.pts.length, 3, `${id} turns once: ${pathOf(r.pts)}`);
});

console.log(`diagram-route: ${passed} passed`);
