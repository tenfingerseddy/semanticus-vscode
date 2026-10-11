// Review round 4 of m11/diagram-route (28 Sep): the page makes room for its lines. Kane's decision on the round 3
// verification: when a saved layout leaves no room for the lines (a person's drop among close neighbours, an
// assistant's grid that is too tight, a card saved on top of or just under another, a card that grows), the page
// moves cards apart just enough and saves the moved spots, the way reflowCards does when a card opens; one Undo puts
// them back, and they stay put (keep). Executes the SAME modules the webview ships: makeRoom, clearDropped and
// reflowCards (webview/src/diagramfit.mjs), and routeAll (webview/src/diagramroute.mjs).
//
// 1. The 80-table model in the assistant's plain grid 20 px apart (the live page's geometry, the round 3 verifier's
//    harness at 1440 x 900): no routing can draw nine lines readably in a 20 px gap. Room made, no two lines are
//    closer than NEAR (3) px along each other for over RUN (4) px, none is drawn along a card's border and none runs
//    behind a card, and the grid is still a grid.
// 2. The same model 40 px apart: the spread used to slide runs onto the channel's edges, which drew lines along cards'
//    borders (17 on this layout); with room made there are none.
// 3. The assistant saves a card on top of another, half over it, or 4 px under it: parted by the shorter push, just
//    far enough for the lines between them.
// 4. A layout that already has room does not move: the Studio's own bus matrix at 30, 80 and 300 tables.
// 5. Kept cards (a put-back, or the assistant asking to keep a spot) never move, in makeRoom and in reflowCards' overlap pass.
// 6. Snap: a card the page moves, or a dropped card it slides, lands on the 20 px grid.
// 7. clearDropped, given the lines: a card dropped closer to another than their lines need moves to where they have
//    room, and no other card moves.
// 8. Lines passing between two cards need room too: where the routed lines still crowd a gap once room is made at
//    ROOM_MIN (200 tables 20 px apart in a square window: lines passing through a 48 px gap), crowdedRuns finds them.
//    The page's make-room (roomSteps, review round 5: gapLoads over two cheap routings of every line, roomForLoads,
//    makeRoom again, ROOM_ROUNDS at most, before anything is drawn) leaves a quarter of that crowding or less once
//    routed, and nothing a person would see as two lines on top of each other or a line on a card's border (the round
//    3 verifier's measures: within 1.5 px along each other for over 8 px, within 0.5 px of a border for over 8 px).
import assert from 'node:assert/strict';
import { makeRoom, roomBetween, clearDropped, reflowCards, roomSteps, ROOM_MIN, CARD_GAP } from '../webview/src/diagramfit.mjs';
import { routeAll, busGrid, crowdedRuns, drain, NEAR, RUN, HUG } from '../webview/src/diagramroute.mjs';

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};
const overlap = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;

// A live page's cards and lines, [x, y, w, h] and [from_to, sx, sy, side, tx, ty, side] in flow px.
const unpack = (fix) => {
  const cards = fix.boxes.map(([x, y, w, h], i) => ({ id: fix.names[i], x, y, w, h }));
  const lines = fix.lines.map(([id, sx, sy, ss, tx, ty, ts]) => ({ id, src: { x: sx, y: sy, side: ss }, dst: { x: tx, y: ty, side: ts } }));
  return { cards, lines };
};
// The card each line end sits on, and how many lines leave each card by each side (what the page reads off anchors).
function ownersOf(cards, lines) {
  const own = (e) => cards.find((c) => (e.side === 'right' ? Math.abs(e.x - c.x - c.w) <= 6 && e.y >= c.y && e.y <= c.y + c.h
    : e.side === 'left' ? Math.abs(e.x - c.x) <= 6 && e.y >= c.y && e.y <= c.y + c.h
    : e.side === 'bottom' ? Math.abs(e.y - c.y - c.h) <= 6 && e.x >= c.x && e.x <= c.x + c.w
    : Math.abs(e.y - c.y) <= 6 && e.x >= c.x && e.x <= c.x + c.w));
  const ends = lines.map((l) => [own(l.src), own(l.dst)]);
  const counts = new Map(cards.map((c) => [c.id, { top: 0, right: 0, bottom: 0, left: 0 }]));
  ends.forEach(([a, b], i) => { if (a) counts.get(a.id)[lines[i].src.side]++; if (b) counts.get(b.id)[lines[i].dst.side]++; });
  return { ends, counts };
}
// Room made, and each line's ends moved with their cards (the handles ride on the card).
function withRoom(cards, lines) {
  const { ends, counts } = ownersOf(cards, lines);
  assert.ok(ends.every(([a, b]) => a && b), 'every line end sits on a card');
  const to = makeRoom(cards, counts);
  const mv = (e, c) => (to[c.id] ? { ...e, x: e.x + to[c.id].x - c.x, y: e.y + to[c.id].y - c.y } : e);
  return { to, cards: cards.map((c) => ({ ...c, ...(to[c.id] ?? {}) })), lines: lines.map((l, i) => ({ ...l, src: mv(l.src, ends[i][0]), dst: mv(l.dst, ends[i][1]) })), counts };
}
// Pairs of lines closer than NEAR along each other for over RUN px; runs within `hug` px of a card they do not join
// for over 8 px; segments strictly inside a card.
function faults(cards, lines, r, near = NEAR, hug = HUG, run = RUN) {
  const segs = [];
  for (const l of lines) { const p = r.get(l.id).pts; assert.ok(p, `${l.id} was routed`); for (let i = 1; i < p.length; i++) segs.push({ id: l.id, a: p[i - 1], b: p[i] }); }
  const close = new Map(), border = [], behind = [];
  for (let i = 0; i < segs.length; i++) for (let j = i + 1; j < segs.length; j++) {
    const s = segs[i], t = segs[j]; if (s.id === t.id) continue;
    const v = s.a.x === s.b.x && t.a.x === t.b.x, h = s.a.y === s.b.y && t.a.y === t.b.y;
    if (!v && !h) continue;
    const d = v ? Math.abs(s.a.x - t.a.x) : Math.abs(s.a.y - t.a.y); if (d >= near) continue;
    const q = v ? 'y' : 'x';
    const ov = Math.min(Math.max(s.a[q], s.b[q]), Math.max(t.a[q], t.b[q])) - Math.max(Math.min(s.a[q], s.b[q]), Math.min(t.a[q], t.b[q]));
    if (ov > 0) { const k = [s.id, t.id].sort().join(' + '); close.set(k, (close.get(k) ?? 0) + ov); }
  }
  for (const s of segs) for (const c of cards) {
    const [from, to] = s.id.replace(/#.*/, '').split('_');
    const x0 = Math.min(s.a.x, s.b.x), x1 = Math.max(s.a.x, s.b.x), y0 = Math.min(s.a.y, s.b.y), y1 = Math.max(s.a.y, s.b.y);
    if (x0 < c.x + c.w - 0.5 && x1 > c.x + 0.5 && y0 < c.y + c.h - 0.5 && y1 > c.y + 0.5) behind.push(`${s.id} behind ${c.id}`);
    if (c.id === from || c.id === to) continue;
    const v = x0 === x1, at = v ? x0 : y0, e0 = v ? c.x : c.y, e1 = e0 + (v ? c.w : c.h);
    const ov = Math.min(v ? y1 : x1, v ? c.y + c.h : c.x + c.w) - Math.max(v ? y0 : x0, v ? c.y : c.x);
    if ((Math.abs(at - e0) < hug || Math.abs(at - e1) < hug) && ov > 8) border.push(`${s.id} along ${c.id} for ${Math.round(ov)} px`);
  }
  return { close: [...close].filter(([, len]) => len > run).map(([k, run]) => `${k}: ${Math.round(run)} px`), border, behind };
}

const PLAIN20 = {"names":["Fact 001","Fact 002","Fact 003","Fact 004","Fact 005","Fact 006","Fact 007","Fact 008","Dim 001","Dim 002","Dim 003","Dim 004","Dim 005","Dim 006","Dim 007","Dim 008","Dim 009","Dim 010","Dim 011","Dim 012","Dim 013","Dim 014","Dim 015","Dim 016","Dim 017","Dim 018","Dim 019","Dim 020","Dim 021","Dim 022","Dim 023","Dim 024","Dim 025","Dim 026","Dim 027","Dim 028","Dim 029","Dim 030","Dim 031","Dim 032","Dim 033","Dim 034","Dim 035","Dim 036","Dim 037","Dim 038","Dim 039","Dim 040","Dim 041","Dim 042","Dim 043","Dim 044","Dim 045","Dim 046","Dim 047","Dim 048","Dim 049","Dim 050","Dim 051","Dim 052","Dim 053","Dim 054","Dim 055","Dim 056","Dim 057","Dim 058","Dim 059","Dim 060","Loose 001","Loose 002","Loose 003","Loose 004","Loose 005","Loose 006","Loose 007","Loose 008","Loose 009","Loose 010","Loose 011","Loose 012"],"boxes":[[210,0,190,300],[420,0,190,300],[630,0,190,300],[840,0,190,300],[1050,0,190,300],[1260,0,190,300],[1470,0,190,300],[1680,0,190,300],[1890,0,190,92],[2100,0,190,92],[2310,0,190,92],[2520,0,190,92],[2730,0,190,92],[0,320,190,92],[0,0,190,92],[210,320,190,92],[420,320,190,92],[630,320,190,92],[840,320,190,92],[1470,1280,190,92],[1050,320,190,92],[1680,1280,190,92],[1260,320,190,92],[1470,320,190,92],[1890,1280,190,92],[1680,320,190,92],[2100,1280,190,92],[1890,320,190,92],[2100,320,190,92],[2310,320,190,92],[2520,320,190,92],[2310,1280,190,92],[2730,320,190,92],[0,639.999,190,92],[210,639.999,190,92],[420,639.999,190,92],[630,639.999,190,92],[840,639.999,190,92],[1050,639.999,190,92],[1260,639.999,190,92],[1470,639.999,190,92],[1680,639.999,190,92],[1890,639.999,190,92],[2100,639.999,190,92],[2310,639.999,190,92],[2520,639.999,190,92],[2730,639.999,190,92],[0,959.999,190,92],[210,959.999,190,92],[420,959.999,190,92],[630,959.999,190,92],[840,959.999,190,92],[1050,959.999,190,92],[1260,959.999,190,92],[1470,959.999,190,92],[1680,959.999,190,92],[1890,959.999,190,92],[2100,959.999,190,92],[2310,959.999,190,92],[2520,959.999,190,92],[2730,959.999,190,92],[0,1280,190,92],[210,1280,190,92],[420,1280,190,92],[630,1280,190,92],[840,1280,190,92],[1050,1280,190,92],[1260,1280,190,92],[2520,1280,190,92],[2730,1280,190,92],[0,1600,190,92],[210,1600,190,92],[420,1600,190,92],[630,1600,190,92],[840,1600,190,92],[1050,1600,190,92],[1260,1600,190,92],[1470,1600,190,92],[1680,1600,190,92],[1890,1600,190,92]],"lines":[["Fact 001_Dim 001",404,30,"right",2016.657,96,"bottom"],["Fact 001_Dim 006",404,90,"right",126.656,416,"bottom"],["Fact 001_Dim 011",404,60,"right",966.656,416,"bottom"],["Fact 001_Dim 016",404,120,"right",1596.656,416,"bottom"],["Fact 001_Dim 021",404,150,"right",2195,416,"bottom"],["Fact 001_Dim 026",404,180,"right",95,735.999,"bottom"],["Fact 001_Dim 031",404,270,"right",1145,735.999,"bottom"],["Fact 001_Dim 036",404,210,"right",2226.657,735.999,"bottom"],["Fact 001_Dim 041",404,240,"right",336.656,1055.999,"bottom"],["Fact 002_Dim 008",614,120,"right",336.656,416,"bottom"],["Fact 002_Dim 013",614,60,"right",1176.656,416,"bottom"],["Fact 002_Dim 018",614,150,"right",1806.657,416,"bottom"],["Fact 002_Dim 023",614,180,"right",2646.657,416,"bottom"],["Fact 002_Dim 028",614,210,"right",515,735.999,"bottom"],["Fact 002_Dim 033",614,270,"right",1565,735.999,"bottom"],["Fact 002_Dim 038",614,30,"right",2615,735.999,"bottom"],["Fact 002_Dim 043",614,240,"right",756.656,1055.999,"bottom"],["Fact 002_Dim 048",614,90,"right",1822.5,1055.999,"bottom"],["Fact 003_Dim 015",824,60,"right",1386.656,416,"bottom"],["Fact 003_Dim 020",824,180,"right",2016.657,416,"bottom"],["Fact 003_Dim 025",824,210,"right",2856.657,416,"bottom"],["Fact 003_Dim 030",824,240,"right",966.656,735.999,"bottom"],["Fact 003_Dim 035",824,270,"right",1985,735.999,"bottom"],["Fact 003_Dim 040",824,90,"right",95,1055.999,"bottom"],["Fact 003_Dim 045",824,150,"right",1145,1055.999,"bottom"],["Fact 003_Dim 050",824,120,"right",2242.501,1055.999,"bottom"],["Fact 003_Dim 055",824,30,"right",336.656,1375.999,"bottom"],["Fact 004_Dim 022",1034,150,"right",2405.001,416,"bottom"],["Fact 004_Dim 027",1034,210,"right",305,735.999,"bottom"],["Fact 004_Dim 032",1034,240,"right",1355,735.999,"bottom"],["Fact 004_Dim 037",1034,270,"right",2405.001,735.999,"bottom"],["Fact 004_Dim 042",1034,60,"right",515,1055.999,"bottom"],["Fact 004_Dim 047",1034,120,"right",1565,1055.999,"bottom"],["Fact 004_Dim 052",1034,30,"right",2583.328,1055.999,"bottom"],["Fact 004_Dim 057",1034,180,"right",725,1375.999,"bottom"],["Fact 004_Dim 002",1034,90,"right",2195,96,"bottom"],["Fact 005_Dim 029",1244,240,"right",725,735.999,"bottom"],["Fact 005_Dim 034",1244,270,"right",1775,735.999,"bottom"],["Fact 005_Dim 039",1244,60,"right",2825,735.999,"bottom"],["Fact 005_Dim 044",1244,90,"right",935,1055.999,"bottom"],["Fact 005_Dim 049",1244,180,"right",1985,1055.999,"bottom"],["Fact 005_Dim 054",1244,30,"right",126.656,1375.999,"bottom"],["Fact 005_Dim 059",1244,210,"right",1145,1375.999,"bottom"],["Fact 005_Dim 004",1244,120,"right",2615.001,96,"bottom"],["Fact 005_Dim 009",1244,150,"right",515,416,"bottom"],["Fact 006_Dim 036",1454,180,"right",2163.329,735.999,"bottom"],["Fact 006_Dim 041",1454,210,"right",273.329,1055.999,"bottom"],["Fact 006_Dim 046",1454,240,"right",1386.656,1055.999,"bottom"],["Fact 006_Dim 051",1454,270,"right",2405.001,1055.999,"bottom"],["Fact 006_Dim 056",1454,90,"right",483.328,1375.999,"bottom"],["Fact 006_Dim 001",1454,30,"right",1953.328,96,"bottom"],["Fact 006_Dim 006",1454,120,"right",63.328,416,"bottom"],["Fact 006_Dim 011",1454,60,"right",903.328,416,"bottom"],["Fact 006_Dim 016",1454,150,"right",1533.328,416,"bottom"],["Fact 007_Dim 043",1664,240,"right",693.328,1055.999,"bottom"],["Fact 007_Dim 048",1664,60,"right",1775,1055.999,"bottom"],["Fact 007_Dim 053",1664,270,"right",2825,1055.999,"bottom"],["Fact 007_Dim 058",1664,180,"right",903.328,1375.999,"bottom"],["Fact 007_Dim 003",1664,90,"right",2405,96,"bottom"],["Fact 007_Dim 008",1664,120,"right",273.328,416,"bottom"],["Fact 007_Dim 013",1664,30,"right",1113.328,416,"bottom"],["Fact 007_Dim 018",1664,150,"right",1743.328,416,"bottom"],["Fact 007_Dim 023",1664,210,"right",2583.329,416,"bottom"],["Fact 008_Dim 050",1874,60,"right",2195.001,1055.999,"bottom"],["Fact 008_Dim 055",1874,270,"right",273.328,1375.999,"bottom"],["Fact 008_Dim 060",1874,210,"right",1386.656,1375.999,"bottom"],["Fact 008_Dim 005",1874,90,"right",2825.001,96,"bottom"],["Fact 008_Dim 010",1874,240,"right",725,416,"bottom"],["Fact 008_Dim 015",1874,30,"right",1323.328,416,"bottom"],["Fact 008_Dim 020",1874,120,"right",1953.329,416,"bottom"],["Fact 008_Dim 025",1874,150,"right",2793.329,416,"bottom"],["Fact 008_Dim 030",1874,180,"right",903.328,735.999,"bottom"],["Dim 001_Dim 060",2084,46,"right",1323.328,1375.999,"bottom"],["Dim 003_Dim 058",2504,46,"right",966.656,1375.999,"bottom"],["Dim 005_Dim 056",2924.001,46,"right",546.656,1375.999,"bottom"],["Dim 007_Dim 054",194,46,"right",63.328,1375.999,"bottom"],["Dim 009_Dim 052",614,366,"right",2646.657,1055.999,"bottom"],["Dim 011_Dim 050",1034,366,"right",2147.501,1055.999,"bottom"],["Dim 013_Dim 048",1244,366,"right",1727.5,1055.999,"bottom"],["Dim 015_Dim 046",1454,366,"right",1323.328,1055.999,"bottom"]]};
const PLAIN40 = {"names":["Fact 001","Fact 002","Fact 003","Fact 004","Fact 005","Fact 006","Fact 007","Fact 008","Dim 001","Dim 002","Dim 003","Dim 004","Dim 005","Dim 006","Dim 007","Dim 008","Dim 009","Dim 010","Dim 011","Dim 012","Dim 013","Dim 014","Dim 015","Dim 016","Dim 017","Dim 018","Dim 019","Dim 020","Dim 021","Dim 022","Dim 023","Dim 024","Dim 025","Dim 026","Dim 027","Dim 028","Dim 029","Dim 030","Dim 031","Dim 032","Dim 033","Dim 034","Dim 035","Dim 036","Dim 037","Dim 038","Dim 039","Dim 040","Dim 041","Dim 042","Dim 043","Dim 044","Dim 045","Dim 046","Dim 047","Dim 048","Dim 049","Dim 050","Dim 051","Dim 052","Dim 053","Dim 054","Dim 055","Dim 056","Dim 057","Dim 058","Dim 059","Dim 060","Loose 001","Loose 002","Loose 003","Loose 004","Loose 005","Loose 006","Loose 007","Loose 008","Loose 009","Loose 010","Loose 011","Loose 012"],"boxes":[[230,0,190,300],[460,0,190,300],[690,0,190,300],[920,0,190,300],[1150,0,190,300],[1380,0,190,300],[1610,0,190,300],[1840,0,190,300],[2070,0,190,92],[2300,0,190,92],[2530,0,190,92],[2760,0,190,92],[2990,0,190,92],[0,340,190,92],[0,0,190,92],[230,340,190,92],[460,340,190,92],[690,340,190,92],[920,340,190,92],[1610,1360,190,92],[1150,340,190,92],[1840,1360,190,92],[1380,340,190,92],[1610,340,190,92],[2070,1360,190,92],[1840,340,190,92],[2300,1360,190,92],[2070,340,190,92],[2300,340,190,92],[2530,340,190,92],[2760,340,190,92],[2530,1360,190,92],[2990,340,190,92],[0,680,190,92],[230,680,190,92],[460,680,190,92],[690,680,190,92],[920,680,190,92],[1150,680,190,92],[1380,680,190,92],[1610,680,190,92],[1840,680,190,92],[2070,680,190,92],[2300,680,190,92],[2530,680,190,92],[2760,680,190,92],[2990,680,190,92],[0,1020,190,92],[230,1020,190,92],[460,1020,190,92],[690,1020,190,92],[920,1020,190,92],[1150,1020,190,92],[1380,1020,190,92],[1610,1020,190,92],[1840,1020,190,92],[2070,1020,190,92],[2300,1020,190,92],[2530,1020,190,92],[2760,1020,190,92],[2990,1020,190,92],[0,1360,190,92],[230,1360,190,92],[460,1360,190,92],[690,1360,190,92],[920,1360,190,92],[1150,1360,190,92],[1380,1360,190,92],[2760,1360,190,92],[2990,1360,190,92],[0,1700,190,92],[230,1700,190,92],[460,1700,190,92],[690,1700,190,92],[920,1700,190,92],[1150,1700,190,92],[1380,1700,190,92],[1610,1700,190,92],[1840,1700,190,92],[2070,1700,190,92]],"lines":[["Fact 001_Dim 001",424,30,"right",2196.657,96,"bottom"],["Fact 001_Dim 006",424,90,"right",126.656,436,"bottom"],["Fact 001_Dim 011",424,60,"right",1046.657,436,"bottom"],["Fact 001_Dim 016",424,120,"right",1736.657,436,"bottom"],["Fact 001_Dim 021",424,150,"right",2395.001,436,"bottom"],["Fact 001_Dim 026",424,180,"right",95,776,"bottom"],["Fact 001_Dim 031",424,270,"right",1245.001,776,"bottom"],["Fact 001_Dim 036",424,210,"right",2426.657,776,"bottom"],["Fact 001_Dim 041",424,240,"right",356.656,1116,"bottom"],["Fact 002_Dim 008",654,120,"right",356.656,436,"bottom"],["Fact 002_Dim 013",654,60,"right",1276.657,436,"bottom"],["Fact 002_Dim 018",654,150,"right",1966.657,436,"bottom"],["Fact 002_Dim 023",654,180,"right",2886.657,436,"bottom"],["Fact 002_Dim 028",654,210,"right",555,776,"bottom"],["Fact 002_Dim 033",654,270,"right",1705.001,776,"bottom"],["Fact 002_Dim 038",654,30,"right",2855.001,776,"bottom"],["Fact 002_Dim 043",654,240,"right",816.657,1116,"bottom"],["Fact 002_Dim 048",654,90,"right",1982.501,1116,"bottom"],["Fact 003_Dim 015",884,60,"right",1506.657,436,"bottom"],["Fact 003_Dim 020",884,180,"right",2196.657,436,"bottom"],["Fact 003_Dim 025",884,210,"right",3116.658,436,"bottom"],["Fact 003_Dim 030",884,240,"right",1046.657,776,"bottom"],["Fact 003_Dim 035",884,270,"right",2165.001,776,"bottom"],["Fact 003_Dim 040",884,90,"right",95,1116,"bottom"],["Fact 003_Dim 045",884,150,"right",1245.001,1116,"bottom"],["Fact 003_Dim 050",884,120,"right",2442.501,1116,"bottom"],["Fact 003_Dim 055",884,30,"right",356.656,1456,"bottom"],["Fact 004_Dim 022",1114,150,"right",2625.001,436,"bottom"],["Fact 004_Dim 027",1114,210,"right",325,776,"bottom"],["Fact 004_Dim 032",1114,240,"right",1475,776,"bottom"],["Fact 004_Dim 037",1114,270,"right",2625.001,776,"bottom"],["Fact 004_Dim 042",1114,60,"right",555,1116,"bottom"],["Fact 004_Dim 047",1114,120,"right",1705,1116,"bottom"],["Fact 004_Dim 052",1114,30,"right",2823.329,1116,"bottom"],["Fact 004_Dim 057",1114,180,"right",785,1456,"bottom"],["Fact 004_Dim 002",1114,90,"right",2395.001,96,"bottom"],["Fact 005_Dim 029",1344.001,240,"right",785,776,"bottom"],["Fact 005_Dim 034",1344.001,270,"right",1935.001,776,"bottom"],["Fact 005_Dim 039",1344.001,60,"right",3085.001,776,"bottom"],["Fact 005_Dim 044",1344.001,90,"right",1015,1116,"bottom"],["Fact 005_Dim 049",1344.001,180,"right",2165.001,1116,"bottom"],["Fact 005_Dim 054",1344.001,30,"right",126.656,1456,"bottom"],["Fact 005_Dim 059",1344.001,210,"right",1245,1456,"bottom"],["Fact 005_Dim 004",1344.001,120,"right",2855.001,96,"bottom"],["Fact 005_Dim 009",1344.001,150,"right",555,436,"bottom"],["Fact 006_Dim 036",1574.001,180,"right",2363.329,776,"bottom"],["Fact 006_Dim 041",1574.001,210,"right",293.329,1116,"bottom"],["Fact 006_Dim 046",1574.001,240,"right",1506.657,1116,"bottom"],["Fact 006_Dim 051",1574.001,270,"right",2625.001,1116,"bottom"],["Fact 006_Dim 056",1574.001,90,"right",523.328,1456,"bottom"],["Fact 006_Dim 001",1574.001,30,"right",2133.329,96,"bottom"],["Fact 006_Dim 006",1574.001,120,"right",63.328,436,"bottom"],["Fact 006_Dim 011",1574.001,60,"right",983.328,436,"bottom"],["Fact 006_Dim 016",1574.001,150,"right",1673.329,436,"bottom"],["Fact 007_Dim 043",1804.001,240,"right",753.328,1116,"bottom"],["Fact 007_Dim 048",1804.001,60,"right",1935.001,1116,"bottom"],["Fact 007_Dim 053",1804.001,270,"right",3085.001,1116,"bottom"],["Fact 007_Dim 058",1804.001,180,"right",983.328,1456,"bottom"],["Fact 007_Dim 003",1804.001,90,"right",2625.001,96,"bottom"],["Fact 007_Dim 008",1804.001,120,"right",293.328,436,"bottom"],["Fact 007_Dim 013",1804.001,30,"right",1213.329,436,"bottom"],["Fact 007_Dim 018",1804.001,150,"right",1903.329,436,"bottom"],["Fact 007_Dim 023",1804.001,210,"right",2823.329,436,"bottom"],["Fact 008_Dim 050",2034.001,60,"right",2395.001,1116,"bottom"],["Fact 008_Dim 055",2034.001,270,"right",293.328,1456,"bottom"],["Fact 008_Dim 060",2034.001,210,"right",1506.657,1456,"bottom"],["Fact 008_Dim 005",2034.001,90,"right",3085.001,96,"bottom"],["Fact 008_Dim 010",2034.001,240,"right",785,436,"bottom"],["Fact 008_Dim 015",2034.001,30,"right",1443.329,436,"bottom"],["Fact 008_Dim 020",2034.001,120,"right",2133.329,436,"bottom"],["Fact 008_Dim 025",2034.001,150,"right",3053.33,436,"bottom"],["Fact 008_Dim 030",2034.001,180,"right",983.328,776,"bottom"],["Dim 001_Dim 060",2264.001,46,"right",1443.329,1456,"bottom"],["Dim 003_Dim 058",2724.001,46,"right",1046.657,1456,"bottom"],["Dim 005_Dim 056",3184.001,46,"right",586.656,1456,"bottom"],["Dim 007_Dim 054",194,46,"right",63.328,1456,"bottom"],["Dim 009_Dim 052",654,386,"right",2886.657,1116,"bottom"],["Dim 011_Dim 050",1114,386,"right",2347.501,1116,"bottom"],["Dim 013_Dim 048",1344.001,386,"right",1887.501,1116,"bottom"],["Dim 015_Dim 046",1574.001,386,"right",1443.328,1116,"bottom"]]};

ok("80 tables in the assistant's grid 20 px apart: room made, no two lines closer than 3 px, none on a card's border or behind one, and still a grid", () => {
  const raw = unpack(PLAIN20);
  const before = faults(raw.cards, raw.lines, routeAll(raw.lines, raw.cards));
  assert.ok(before.close.length > 0, 'the fixture must reproduce round 3: lines on top of each other in the 20 px grid');
  const { to, cards, lines } = withRoom(raw.cards, raw.lines);
  assert.ok(Object.keys(to).length > 0, 'the page makes room');
  for (let i = 0; i < cards.length; i++) for (let j = i + 1; j < cards.length; j++) assert.ok(!overlap(cards[i], cards[j]), `${cards[i].id} on ${cards[j].id}`);
  const f = faults(cards, lines, routeAll(lines, cards));
  assert.deepEqual(f.close.slice(0, 5), [], 'lines closer than 3 px along each other');
  assert.deepEqual(f.border.slice(0, 5), [], "lines along a card's border");
  assert.deepEqual(f.behind.slice(0, 5), [], 'lines behind a card');
  // Still a grid: cards that shared a column (or a row) before still share it.
  for (const ax of ['x', 'y']) {
    const groups = new Map();
    raw.cards.forEach((c, i) => { const k = Math.round(c[ax]); if (!groups.has(k)) groups.set(k, []); groups.get(k).push(cards[i][ax]); });
    for (const [k, vs] of groups) assert.ok(vs.every((v) => Math.abs(v - vs[0]) < 0.5), `the ${ax === 'x' ? 'column' : 'row'} at ${k} came apart: ${vs.slice(0, 4).join(', ')}`);
  }
});

ok("80 tables in the assistant's grid 40 px apart: room made, no line along a card's border", () => {
  const raw = unpack(PLAIN40);
  const before = faults(raw.cards, raw.lines, routeAll(raw.lines, raw.cards));
  const { cards, lines } = withRoom(raw.cards, raw.lines);
  const f = faults(cards, lines, routeAll(lines, cards));
  assert.deepEqual(f.border.slice(0, 5), [], `lines along a card's border (${before.border.length} before room was made)`);
  assert.deepEqual(f.close.slice(0, 5), [], 'lines closer than 3 px along each other');
  assert.deepEqual(f.behind.slice(0, 5), [], 'lines behind a card');
});

ok('roomBetween: 9 px for the end markers and 3 px a line beside each side with lines, 4 px clear, never under ROOM_MIN', () => {
  assert.equal(ROOM_MIN, CARD_GAP);
  assert.equal(roomBetween(0, 0), ROOM_MIN);
  assert.equal(roomBetween(9, 0), ROOM_MIN);            // 9 + 27 + 4 = 40, under the floor
  assert.equal(roomBetween(12, 0), 9 + 36 + 4);
  assert.equal(roomBetween(9, 9), 2 * (9 + 27) + 4);
});

ok('the assistant saves a card on top of another, half over it or 4 px under it: parted the shorter way, just far enough', () => {
  const lines = { Date: { top: 0, right: 0, bottom: 2, left: 0 }, Supplier: { top: 0, right: 0, bottom: 14, left: 0 } };
  const need = (up) => roomBetween(lines[up].bottom, 0);
  for (const [label, under, over, at] of [['30 and 20 px off', 'Supplier', 'Date', { x: 30, y: 20 }], ['half over', 'Supplier', 'Date', { x: 0, y: 46 }], ['4 px under', 'Date', 'Customer', { x: 0, y: 96 }]]) {
    const cards = [{ id: under, x: 0, y: 0, w: 190, h: 92 }, { id: over, ...at, w: 190, h: 92 }, { id: 'Far', x: 900, y: 900, w: 190, h: 92 }];
    const to = makeRoom(cards, lines);
    assert.deepEqual(Object.keys(to), [over], `${label}: only the card saved on top moves`);
    assert.equal(to[over].x, at.x, `${label}: it keeps its x (the push down is the shorter one)`);
    assert.equal(to[over].y, 92 + need(under), `${label}: it sits just the lines' room under ${under}`);
  }
});

ok("a layout that already has room does not move: the Studio's own bus matrix at 30, 80 and 300 tables", () => {
  for (const [nf, nd, ni, per] of [[4, 20, 6, 7], [8, 60, 12, 9], [30, 226, 44, 9]]) {
    const facts = Array.from({ length: nf }, () => ({ w: 190, h: 300 })), dims = Array.from({ length: nd }, () => ({ w: 190, h: 92 })), iso = Array.from({ length: ni }, () => ({ w: 190, h: 92 }));
    const g = busGrid(dims, facts, iso, 1.6, { nodeW: 190, nodeH: 92 });
    const cards = [...g.facts.map((p, i) => ({ id: 'F' + i, ...p, ...facts[i] })), ...g.dims.map((p, i) => ({ id: 'D' + i, ...p, ...dims[i] })), ...g.iso.map((p, i) => ({ id: 'L' + i, ...p, ...iso[i] }))];
    const lines = {};
    for (let f = 0; f < nf; f++) lines['F' + f] = { top: 0, right: per, bottom: 0, left: 0 };
    for (let f = 0; f < nf; f++) for (let k = 0; k < per; k++) { const d = 'D' + ((f * 7 + k * 5) % nd); lines[d] ??= { top: 0, right: 0, bottom: 0, left: 0 }; lines[d].bottom++; }
    assert.deepEqual(makeRoom(cards, lines), {}, `${nf + nd + ni} tables`);
  }
});

ok("one card the assistant saves 4 px under another in the Studio's own bus matrix: only it and the cards below it in its column move", () => {
  const facts = Array.from({ length: 4 }, () => ({ w: 190, h: 300 })), dims = Array.from({ length: 20 }, () => ({ w: 190, h: 92 }));
  const g = busGrid(dims, facts, [], 1.6, { nodeW: 190, nodeH: 92 });
  const cards = [...g.facts.map((p, i) => ({ id: 'F' + i, ...p, ...facts[i] })), ...g.dims.map((p, i) => ({ id: 'D' + i, ...p, ...dims[i] }))];
  const top = cards.find((c) => c.id === 'D0'), mover = cards.find((c) => c.id === 'D7');
  mover.x = top.x; mover.y = top.y + top.h + 4;
  const lines = { D0: { top: 0, right: 0, bottom: 2, left: 0 } };
  const to = makeRoom(cards, lines);
  assert.equal(to.D7.y, top.y + top.h + roomBetween(2, 0), 'the saved card sits just the lines\' room under the other');
  const others = Object.keys(to).filter((id) => id !== 'D7');
  for (const id of others) { const c = cards.find((x) => x.id === id); assert.ok(Math.abs(c.x - top.x) < 0.5 && c.y > top.y, `${id} moved, though it is not below in that column`); }
});

ok('kept cards never move (makeRoom and the reflow overlap pass), while a card after one is still pushed', () => {
  const cards = [{ id: 'A', x: 0, y: 0, w: 190, h: 92 }, { id: 'B', x: 0, y: 96, w: 190, h: 92 }, { id: 'C', x: 0, y: 192, w: 190, h: 92 }];
  const none = {};
  assert.deepEqual(Object.keys(makeRoom(cards, none)), ['B', 'C']);
  const kept = makeRoom(cards, none, 0, new Set(['B']));
  assert.deepEqual(Object.keys(kept), ['C'], 'B is kept: only C moves');
  assert.equal(kept.C.y, 96 + 92 + ROOM_MIN, 'C is pushed from where the kept B is');
  assert.deepEqual(makeRoom(cards, none, 0, new Set(['B', 'C'])), {}, 'a put-back of both stays');
  // reflowCards: an overlap is pushed clear, unless the card is kept; a card growing above a kept one still moves it.
  const stack = [{ id: 'A', x: 0, y: 0, w: 190, h: 92 }, { id: 'B', x: 0, y: 50, w: 190, h: 92 }];
  assert.equal(reflowCards(stack, {}).B.y, 92 + CARD_GAP);
  assert.equal(reflowCards(stack, {}, CARD_GAP, new Set(['B'])).B.y, 50, 'a kept card stays on the card under it');
  const grew = reflowCards([{ id: 'A', x: 0, y: 0, w: 190, h: 192 }, { id: 'B', x: 0, y: 140, w: 190, h: 92 }], { A: { w: 190, h: 92 } }, CARD_GAP, new Set(['B']));
  assert.equal(grew.B.y, 240, 'A grew by 100: the kept B still moves by that growth');
});

ok('Snap: a card the page moves, and a dropped card it slides, lands on the 20 px grid', () => {
  const cards = [{ id: 'A', x: 0, y: 0, w: 190, h: 92 }, { id: 'B', x: 3, y: 101, w: 190, h: 92 }];
  const to = makeRoom(cards, {}, 20);
  assert.equal(to.B.y % 20, 0); assert.ok(to.B.y >= 92 + ROOM_MIN);
  const drop = clearDropped([{ id: 'A', x: 0, y: 0, w: 190, h: 92 }, { id: 'D', x: 30, y: 20, w: 190, h: 92 }], ['D'], CARD_GAP, 20);
  assert.equal(drop.D.x % 20, 0); assert.equal(drop.D.y % 20, 0);
  assert.ok(!overlap({ ...drop.D, w: 190, h: 92 }, { x: 0, y: 0, w: 190, h: 92 }));
});

ok("clearDropped, given the lines: a card dropped 8 px under another moves to where the lines between them have room, and nothing else moves", () => {
  const cards = [{ id: 'Date', x: 0, y: 0, w: 190, h: 92 }, { id: 'Customer', x: 0, y: 100, w: 190, h: 92 }, { id: 'Far', x: 600, y: 0, w: 190, h: 92 }];
  const lines = { Date: { top: 0, right: 0, bottom: 14, left: 0 } };
  const out = clearDropped(cards, ['Customer'], CARD_GAP, 0, lines);
  assert.deepEqual(Object.keys(out), ['Customer']);
  assert.deepEqual(out.Customer, { x: 0, y: 92 + roomBetween(14, 0) }, 'the shortest way: straight down, just far enough');
  assert.deepEqual(clearDropped(cards, ['Customer']), {}, 'without the lines (a layout that is not routed) only a card on another moves');
});

// The round 3 builder's assistant grid (test/diagram-route-r3.test.mjs): facts, dimensions and unlinked tables in a
// plain grid, each fact's lines leaving its right side for its dimensions' bottoms.
function assistantGrid(nFacts, nDims, nIso, perFact, gap, aspect = 1440 / 900) {
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
    return { id: `F${f}_D${d}#${i}`, src: { x: F.x + F.w + 4, y: F.y + 30 * ((i % perFact) + 1), side: 'right' },
      dst: { x: D.x + (D.w * (k + 1)) / (into.get(d) + 1), y: D.y + D.h + 4, side: 'bottom' } };
  });
  return { cards, lines };
}
ok('200 tables 20 px apart in a square window: where routed lines still crowd a gap, the page widens that gap until nothing crowds', () => {
  const g = assistantGrid(20, 150, 30, 9, 20, 1);
  const { cards: roomy, lines: roomyLines } = withRoom(g.cards, g.lines);
  const first = crowdedRuns(routeAll(roomyLines, roomy), roomy);
  assert.ok(first.length > 0, 'the fixture must crowd a gap once room is made at ROOM_MIN (lines passing through it)');
  // The page's make-room from the assistant's grid: each line's two ends, as the side and how far along it they sit.
  const { ends, counts } = ownersOf(g.cards, g.lines);
  const along = (e, c) => (e.side === 'right' || e.side === 'left' ? (e.y - c.y) / c.h : (e.x - c.x) / c.w);
  const links = g.lines.map((l, i) => ({ id: l.id, from: { id: ends[i][0].id, side: l.src.side, frac: along(l.src, ends[i][0]) }, to: { id: ends[i][1].id, side: l.dst.side, frac: along(l.dst, ends[i][1]) } }));
  const to = drain(roomSteps(g.cards, links, counts));
  const cards = g.cards.map((c) => ({ ...c, ...(to[c.id] ?? {}) }));
  const lines = g.lines.map((l, i) => {
    const mv = (e, c) => { const n = to[c.id]; return n ? { ...e, x: e.x + n.x - c.x, y: e.y + n.y - c.y } : e; };
    return { ...l, src: mv(l.src, ends[i][0]), dst: mv(l.dst, ends[i][1]) };
  });
  const r = routeAll(lines, cards);
  const left = crowdedRuns(r, cards);
  assert.ok(left.length * 4 <= first.length, `${left.length} runs still crowded (${first.length} at ROOM_MIN)`);
  const f = faults(cards, lines, r, 1.5, 0.5, 8);
  assert.deepEqual([...f.close, ...f.border, ...f.behind].slice(0, 5), []);
});

console.log(`diagram-route-r4: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
