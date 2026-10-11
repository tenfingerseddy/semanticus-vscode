// verify: round 3 of the m11/diagram-route verification (28 Sep), left failing on purpose for the builder.
// Executes the SAME code the webview ships: clearDropped (webview/src/diagramfit.mjs) and routeAll
// (webview/src/diagramroute.mjs), on the live page's own geometry and on a tidy grid the person's assistant saves.
//
// Round 3 added "a card dropped on another slides clear of it". clearDropped tries the four ways out of the one card
// it covers, each CARD_GAP (48 px) beyond it, and when every way lands on another card it takes the nearest anyway and
// tries again from there. Where cards sit closer than a card plus two gaps on every side (the Studio's own bus matrix
// puts lookup tables 96 px apart across and 110 px down, and an assistant's grid 80 px apart), every way lands on a
// neighbour, the card steps back and forth between two neighbours for its 50 tries, and it is saved on top of a card:
// the covered card's handles are hidden and its lines run behind the dropped card. The live page does exactly this
// (tools/uishot/diagram-route-verify-r3-drive.mjs sections 1 and 2).
//
// 1. The Studio's own bus matrix at 80 tables (the live page's geometry): a lookup table dropped onto another lookup
//    table, at 900 x 900 (60 of 240 such drops are left on top) and with every card open at 1440 x 900 (27 of 240).
// 2. The assistant's grid 80 px apart: a card dropped onto a card in the middle of it.
// 3. The other door, in real VS Code: the person's assistant saves Customer exactly 4 px under Date over MCP. The
//    lines Sales -> Date and Returns -> Date both run along the 4 px slit between the two cards, exactly on top of each
//    other for 63 px. spread() would slide one of them to Date's edge, but its neighbour check reads the handle, which
//    sits 0.0000036 px inside Customer (a measured card's float noise, the case EPS already covers in segmentHitsBox and
//    covers()), as the stub crossing Customer, and so rejects every place the run could go.
import assert from 'node:assert/strict';
import { clearDropped } from '../webview/src/diagramfit.mjs';
import { routeAll, pathOf } from '../webview/src/diagramroute.mjs';

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};
const covers = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
const OFFSETS = [[0, 0], [30, 20], [-40, 30], [10, 60]];

// Drop `mover` onto each target at each offset; the cards clearDropped leaves still covering another card.
function dropsLeftCovering(cards, targets, pickMover) {
  const bad = [];
  let tries = 0;
  for (const X of targets) for (const [ox, oy] of OFFSETS) {
    const mover = pickMover(X);
    const moved = cards.map((c) => (c === mover ? { ...c, x: X.x + ox, y: X.y + oy } : c));
    const out = clearDropped(moved, [mover.id]);
    const me = { ...moved.find((c) => c.id === mover.id), ...(out[mover.id] ?? {}) };
    const still = moved.filter((c) => c.id !== mover.id && covers(me, c)).map((c) => c.id);
    tries++;
    if (still.length) bad.push(`${mover.id} dropped on ${X.id} + (${ox}, ${oy}) is left at (${me.x}, ${me.y}), covering ${still.join(', ')}`);
  }
  return { bad, tries };
}

// ---- 1: the Studio's own bus matrix (the live page's geometry, the harness at the two window shapes) ----
// Cards [id, x, y, w, h] as the page placed and measured them: lookup tables D..., facts F..., unlinked tables L....
const NARROW80 = [["F001",0,1318,190,300],["F002",0,1690,190,300],["F003",0,2226,190,300],["F004",0,2970,190,300],["F005",0,2598,190,300],["F006",0,946,190,300],["F007",0,574,190,300],["F008",0,202,190,300],["D001",286,0,190,92],["D002",572,404,190,92],["D003",2002,0,190,92],["D004",858,404,190,92],["D005",2288,0,190,92],["D006",2574,0,190,92],["D007",0,2062,190,92],["D008",2860,0,190,92],["D009",3146,0,190,92],["D010",1144,404,190,92],["D011",572,0,190,92],["D012",3968,0,190,92],["D013",858,0,190,92],["D014",3968,164,190,92],["D015",1144,0,190,92],["D016",3432,0,190,92],["D017",3968,328,190,92],["D018",3718,0,190,92],["D019",3968,492,190,92],["D020",286,202,190,92],["D021",1430,404,190,92],["D022",1716,404,190,92],["D023",572,202,190,92],["D024",3968,656,190,92],["D025",858,202,190,92],["D026",2002,404,190,92],["D027",2288,404,190,92],["D028",2574,404,190,92],["D029",2860,404,190,92],["D030",1144,202,190,92],["D031",3146,404,190,92],["D032",3432,404,190,92],["D033",3718,404,190,92],["D034",286,606,190,92],["D035",572,606,190,92],["D036",1430,202,190,92],["D037",858,606,190,92],["D038",1144,606,190,92],["D039",1430,606,190,92],["D040",1716,606,190,92],["D041",1716,202,190,92],["D042",2002,606,190,92],["D043",2002,202,190,92],["D044",2288,606,190,92],["D045",2574,606,190,92],["D046",2288,202,190,92],["D047",2860,606,190,92],["D048",1430,0,190,92],["D049",3146,606,190,92],["D050",1716,0,190,92],["D051",3432,606,190,92],["D052",2574,202,190,92],["D053",3718,606,190,92],["D054",2860,202,190,92],["D055",3146,202,190,92],["D056",3432,202,190,92],["D057",286,808,190,92],["D058",3718,202,190,92],["D059",572,808,190,92],["D060",286,404,190,92],["L001",3968,820,190,92],["L002",3968,984,190,92],["L003",3968,1148,190,92],["L004",3968,1312,190,92],["L005",3968,1476,190,92],["L006",3968,1640,190,92],["L007",3968,1804,190,92],["L008",3968,1968,190,92],["L009",3968,2132,190,92],["L010",3968,2296,190,92],["L011",3968,2460,190,92],["L012",3968,2624,190,92]];
const OPEN80 = [["F001",0,1375,190,303],["F002",0,1750,190,327],["F003",0,2548,190,327],["F004",0,3346,190,327],["F005",0,2947,190,327],["F006",0,1000,190,303],["F007",0,601,190,327],["F008",0,202,190,327],["D001",286,0,190,190],["D002",2002,398,190,239],["D003",2002,0,190,288],["D004",2288,437,190,327],["D005",2288,0,190,327],["D006",2574,0,190,327],["D007",0,2149,190,327],["D008",2860,0,190,190],["D009",3146,0,190,238],["D010",2574,437,190,288],["D011",572,0,190,327],["D012",6256,0,190,327],["D013",858,0,190,327],["D014",6256,399,190,327],["D015",1144,0,190,190],["D016",3432,0,190,238],["D017",6256,798,190,288],["D018",3718,0,190,327],["D019",6256,1158,190,327],["D020",4004,0,190,327],["D021",2860,300,190,327],["D022",3146,349,190,189],["D023",4290,0,190,238],["D024",6256,1557,190,288],["D025",4576,0,190,327],["D026",3432,349,190,327],["D027",3718,437,190,327],["D028",4004,437,190,327],["D029",4290,349,190,189],["D030",4862,0,190,238],["D031",4576,437,190,288],["D032",4862,349,190,327],["D033",5148,300,190,327],["D034",5434,437,190,327],["D035",5720,300,190,327],["D036",5148,0,190,190],["D037",6006,437,190,239],["D038",286,698,190,288],["D039",572,874,190,327],["D040",858,874,190,327],["D041",5434,0,190,327],["D042",1144,737,190,327],["D043",5720,0,190,190],["D044",1430,786,190,238],["D045",1716,737,190,287],["D046",6006,0,190,327],["D047",2002,747,190,327],["D048",1430,0,190,327],["D049",2288,874,190,327],["D050",1716,0,190,190],["D051",2574,835,190,239],["D052",286,300,190,288],["D053",2860,737,190,327],["D054",572,437,190,327],["D055",858,437,190,327],["D056",1144,300,190,327],["D057",3146,649,190,189],["D058",1430,437,190,239],["D059",3432,786,190,288],["D060",1716,300,190,327],["L001",6256,1917,190,156],["L002",6256,2145,190,181],["L003",6256,2398,190,205],["L004",6256,2675,190,230],["L005",6256,2977,190,156],["L006",6256,3205,190,181],["L007",6256,3458,190,205],["L008",6256,3735,190,230],["L009",6256,4037,190,156],["L010",6256,4265,190,181],["L011",6256,4518,190,205],["L012",6256,4795,190,230]];
for (const [name, fix] of [['80 tables at 900 x 900', NARROW80], ['80 tables with every card open at 1440 x 900', OPEN80]]) {
  ok(`the Studio's own bus matrix, ${name}: a lookup table dropped onto another slides clear of every card`, () => {
    const cards = fix.map(([id, x, y, w, h]) => ({ id, x, y, w, h }));
    const lookups = cards.filter((c) => c.id.startsWith('D'));
    // The card dropped is a lookup table from far away, so its own spot is not the one next to the target.
    const { bad, tries } = dropsLeftCovering(cards, lookups, (X) => lookups.find((c) => c !== X && Math.hypot(c.x - X.x, c.y - X.y) > 900));
    assert.deepEqual(bad.slice(0, 6), [], `${bad.length} of ${tries} drops leave the card on top of another`);
  });
}

// ---- 2: a tidy grid the assistant saves, 80 px apart ----
ok("a card dropped onto a card in the middle of an assistant's grid 80 px apart slides clear of every card", () => {
  const cards = [];
  for (let r = 0; r < 4; r++) for (let c = 0; c < 6; c++) cards.push({ id: `Dim ${r}${c}`, x: 300 + c * 270, y: r * 172, w: 190, h: 92 });
  cards.push({ id: 'Moving', x: 0, y: 900, w: 190, h: 92 });
  const middle = cards.filter((c) => /Dim [12][1-4]/.test(c.id));
  const { bad, tries } = dropsLeftCovering(cards, middle, () => cards.at(-1));
  assert.deepEqual(bad.slice(0, 6), [], `${bad.length} of ${tries} drops leave the card on top of another`);
});

// ---- 3: the assistant saves a card 4 px under a lookup table (the live page's geometry, real VS Code, 1440 x 900) ----
// Cards [x, y, w, h] and lines [from>to, sx, sy, side, tx, ty, side] as the Diagram routed them, full precision.
const FOUR_PX = {"names":["Account","Brand","Budget","Carrier","Channel","Currency","Customer","Date","Department","Employee","Geography","Inventory","Loose01","Loose02","Loose03","Loose04","Loose05","Loose06","Product","Promotion","Reason","Region","Returns","Sales","Scenario","Segment","ShipMode","Store","Supplier","Warehouse"],"boxes":[[1430,0,190,92],[1716,0,190,92],[0,826,190,240],[2002,202,190,92],[2002,0,190,92],[2252,0,190,92],[286,96,190,92],[286,0,190,92],[286,404,190,92],[572,202,190,92],[572,0,190,92],[0,202,190,240],[2252,164,190,92],[2252,328,190,92],[2252,492,190,92],[2252,656,190,92],[2252,820,190,92],[2252,984,190,92],[572,404,190,92],[858,0,190,92],[858,202,190,92],[858,404,190,92],[0,1138,190,240],[0,514,190,240],[1144,202,190,92],[1144,0,190,92],[1430,202,190,92],[1716,202,190,92],[1144,404,190,92],[1430,404,190,92]],"lines":[["Sales>Date",194.00000718060662,543.9999712775735,"right",349.32811063878677,96.00000359030331,"bottom"],["Sales>Store",194.00000718060662,693.9999712775735,"right",1779.3280388327205,297.99998563878677,"bottom"],["Sales>Geography",194.00000718060662,573.9999712775735,"right",635.3281106387867,96.00000359030331,"bottom"],["Sales>Supplier",194.00000718060662,723.9999712775735,"right",1238.9999712775734,499.99998563878677,"bottom"],["Sales>Scenario",194.00000718060662,633.9999712775735,"right",1207.3280388327205,297.99998563878677,"bottom"],["Sales>Segment",194.00000718060662,603.9999712775735,"right",1238.9999712775734,96.00000359030331,"bottom"],["Sales>ShipMode",194.00000718060662,663.9999712775735,"right",1493.328182444853,297.99998563878677,"bottom"],["Inventory>Promotion",194.00000718060662,261.99997127757354,"right",921.3281106387867,96.00000359030331,"bottom"],["Inventory>Channel",194.00000718060662,352.00000718060664,"right",2065.3280388327207,96.00000359030331,"bottom"],["Inventory>Employee",194.00000718060662,382.00000718060664,"right",635.3281106387867,297.99998563878677,"bottom"],["Inventory>Account",194.00000718060662,291.99997127757354,"right",1556.65625,96.00000359030331,"bottom"],["Inventory>Carrier",194.00000718060662,412.00000718060664,"right",2096.9999712775734,297.99998563878677,"bottom"],["Inventory>Brand",194.00000718060662,322.00000718060664,"right",1842.65625,96.00000359030331,"bottom"],["Inventory>Customer",194.00000718060662,231.99997127757354,"right",381.00000718060664,191.99998563878677,"bottom"],["Returns>Warehouse",194.00000718060662,1347.9999712775734,"right",1524.9999712775734,499.99998563878677,"bottom"],["Returns>Region",194.00000718060662,1287.9999712775734,"right",952.9999712775735,499.99998563878677,"bottom"],["Returns>Department",194.00000718060662,1197.9999712775734,"right",381.00000718060664,499.99998563878677,"bottom"],["Returns>Reason",194.00000718060662,1257.9999712775734,"right",984.65625,297.99998563878677,"bottom"],["Returns>Date",194.00000718060662,1167.9999712775734,"right",412.65625,96.00000359030331,"bottom"],["Returns>Store",194.00000718060662,1317.9999712775734,"right",1842.65625,297.99998563878677,"bottom"],["Returns>Geography",194.00000718060662,1227.9999712775734,"right",698.65625,96.00000359030331,"bottom"],["Budget>Scenario",194.00000718060662,975.9999712775735,"right",1270.65625,297.99998563878677,"bottom"],["Budget>Segment",194.00000718060662,945.9999712775735,"right",1286.4999712775734,96.00000359030331,"bottom"],["Budget>ShipMode",194.00000718060662,1005.9999712775735,"right",1556.65625,297.99998563878677,"bottom"],["Budget>Product",194.00000718060662,915.9999712775735,"right",666.9999712775735,499.99998563878677,"bottom"],["Budget>Promotion",194.00000718060662,885.9999712775735,"right",984.65625,96.00000359030331,"bottom"],["Budget>Channel",194.00000718060662,1035.9999712775734,"right",2128.65625,96.00000359030331,"bottom"],["Budget>Employee",194.00000718060662,855.9999712775735,"right",698.65625,297.99998563878677,"bottom"],["Date>Brand",480.00000718060664,45.99999640969669,"right",1779.3280388327205,96.00000359030331,"bottom"],["Customer>Reason",480.00000718060664,142.00001436121323,"right",921.3281106387867,297.99998563878677,"bottom"],["Promotion>Segment",1051.9999712775734,45.99999640969669,"right",1191.4999712775734,96.00000359030331,"bottom"],["Geography>Account",765.9999712775735,45.99999640969669,"right",1493.328182444853,96.00000359030331,"bottom"]]};
// Pairs of lines within 1.5 px of each other, the same way, for over 8 px (the round 1 to 3 live measure).
function closePairs(r, lines) {
  const runs = (p) => { const v = [], h = []; for (let i = 1; i < p.length; i++) { const a = p[i - 1], b = p[i]; if (a.x === b.x && a.y !== b.y) v.push([a.x, Math.min(a.y, b.y), Math.max(a.y, b.y)]); else if (a.y === b.y && a.x !== b.x) h.push([a.y, Math.min(a.x, b.x), Math.max(a.x, b.x)]); } return { v, h }; };
  const out = [];
  for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {
    const A = r.get(lines[i].id).pts, B = r.get(lines[j].id).pts;
    if (!A || !B) continue;
    const a = runs(A), b = runs(B); let run = 0;
    for (const k of ['v', 'h']) for (const s of a[k]) for (const u of b[k]) if (Math.abs(s[0] - u[0]) < 1.5) run += Math.max(0, Math.min(s[2], u[2]) - Math.max(s[1], u[1]));
    if (run > 8) out.push(`${lines[i].id} runs ${Math.round(run)} px on ${lines[j].id}: ${pathOf(A)} | ${pathOf(B)}`);
  }
  return out;
}
ok('the assistant saves a card exactly 4 px under a lookup table: no two lines are drawn on top of each other', () => {
  const boxes = FOUR_PX.boxes.map(([x, y, w, h], i) => ({ id: FOUR_PX.names[i], x, y, w, h }));
  const lines = FOUR_PX.lines.map(([id, sx, sy, ss, tx, ty, ts]) => ({ id, src: { x: sx, y: sy, side: ss }, dst: { x: tx, y: ty, side: ts } }));
  const customer = boxes.find((b) => b.id === 'Customer'), date = boxes.find((b) => b.id === 'Date');
  assert.equal(customer.y - (date.y + date.h), 4, 'Customer sits 4 px under Date');
  assert.deepEqual(closePairs(routeAll(lines, boxes), lines), []);
});

console.log(`diagram-route-verify-r3: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
