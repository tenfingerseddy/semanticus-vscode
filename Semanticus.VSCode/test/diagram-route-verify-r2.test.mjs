// verify: round 2 of the m11/diagram-route verification (28 Sep), left failing on purpose for the builder.
// Executes the SAME router the webview ships (webview/src/diagramroute.mjs) on geometry taken from the live page.
//
// 1. A line runs straight through a card that sits 4 to 13 px under the card it joins. A bottom handle's stub runs
//    down to its own card's track line. The stub check in routeOnce excuses every box whose grown margin holds the
//    handle, which is meant for the line's own card, but a neighbour within MARGIN + 4 px of the handle is excused
//    too. When the next card down the column is far away, that track line lies below the neighbour, so the stub runs
//    through it and the route is accepted at the full margin. A person's drag, or a layout the person's assistant
//    saves, leaves a card that close easily: the live page drew Fact 011 -> Dim 097 straight through Dim 151, 8 px
//    under Dim 097, after 300 tables were nudged on a 20 px grid. At 14 px and more the line goes round. (Under 4 px
//    the handle itself lies under the lower card, the case the lane's covered-handle fallback owns, so it is not asked.)
// 2. Lines drawn on top of each other in a tidy grid the person's assistant saves over the MCP door (it reaches the
//    page as layoutDidChange). The page's own geometry for the 30-table model: a plain grid 40 px apart (real VS Code
//    over the MCP door: 3 pairs, the worst 57 px) and a star grid, facts down the left, 20 px apart (real VS Code:
//    6 pairs, the worst 137 px). routeAll keeps the least-shared of its 16 tries when every try shares, and a route
//    takes the first margin that has any route at all, so a busy slit keeps lines on top of each other.
// 3. The Studio's own bus matrix, every card open, 150 tables at 1440 x 900: two lines share 169 px.
// "On top of each other" is the router's own measure: sharedLength (within NEAR px, the same way) over 8 px, the bar
// the lane's tests use.
import assert from 'node:assert/strict';
import { routeAll, sharedLength, pathOf } from '../webview/src/diagramroute.mjs';

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
const unpack = (fix) => ({
  boxes: fix.boxes.map(([x, y, w, h]) => ({ x, y, w, h })),
  lines: fix.lines.map(([sx, sy, ss, tx, ty, ts], i) => ({ id: 'L' + i, src: { x: sx, y: sy, side: ss }, dst: { x: tx, y: ty, side: ts } })),
});
const onTop = (r, lines) => {
  const out = [];
  for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {
    const a = r.get(lines[i].id).pts, b = r.get(lines[j].id).pts;
    if (a && b && sharedLength(a, b) > 8) out.push(`${lines[i].id} runs ${Math.round(sharedLength(a, b))} px along ${lines[j].id}`);
  }
  return out;
};

ok('a card 4 to 13 px under another, with more cards further down: no line runs through it', () => {
  const bad = [];
  for (const gap of [4, 6, 8, 10, 12, 13]) {
    const date = { id: 'Date', x: 286, y: 0, w: 190, h: 92 }, promo = { id: 'Promotion', x: 286, y: 92 + gap, w: 190, h: 92 };
    const store = { id: 'Store', x: 286, y: 500, w: 190, h: 92 }, sales = { id: 'Sales', x: 0, y: 250, w: 190, h: 150 };
    const boxes = [date, promo, store, sales];
    const lines = [{ id: 'Sales>Date', src: { x: 194, y: 300, side: 'right' }, dst: { x: 381, y: 96, side: 'bottom' } },
      { id: 'Sales>Store', src: { x: 194, y: 350, side: 'right' }, dst: { x: 381, y: 596, side: 'bottom' } }];
    const r = routeAll(lines, boxes);
    for (const l of lines) {
      const pts = r.get(l.id).pts;
      if (!pts) { bad.push(`${gap} px: ${l.id} has no route`); continue; }
      const t = through(pts, boxes);
      if (t.length) bad.push(`${gap} px: ${l.id} runs through ${t.join(', ')}: ${pathOf(pts)}`);
    }
  }
  assert.deepEqual(bad, []);
});

const GRID40 = {"boxes":[[690,0,190,210],[230,0,190,210],[460,0,190,210],[0,0,190,210],[690,250,190,92],[0,500,190,92],[460,250,190,92],[1380,500,190,92],[230,500,190,92],[230,250,190,92],[1380,250,190,92],[0,250,190,92],[230,750,190,92],[0,750,190,92],[1150,250,190,92],[690,500,190,92],[920,500,190,92],[920,0,190,92],[920,250,190,92],[1150,500,190,92],[1380,0,190,92],[460,500,190,92],[0,1000,190,92],[1150,0,190,92],[920,750,190,92],[690,750,190,92],[1150,750,190,92],[1380,750,190,92],[230,1000,190,92],[460,750,190,92]],"lines":[[884,90,"right",753.33,346,"bottom"],[884,30,"right",1443.33,596,"bottom"],[884,60,"right",1427.5,346,"bottom"],[884,120,"right",63.33,846,"bottom"],[884,150,"right",983.33,596,"bottom"],[884,180,"right",1213.33,596,"bottom"],[424,90,"right",325,596,"bottom"],[424,150,"right",95,346,"bottom"],[424,180,"right",1245,346,"bottom"],[424,30,"right",1015,96,"bottom"],[424,60,"right",1475,96,"bottom"],[424,120,"right",95,596,"bottom"],[654,150,"right",325,846,"bottom"],[654,30,"right",785,596,"bottom"],[654,60,"right",1015,346,"bottom"],[654,180,"right",555,596,"bottom"],[654,90,"right",555,346,"bottom"],[654,120,"right",325,346,"bottom"],[194,120,"right",1046.66,596,"bottom"],[194,180,"right",1276.66,596,"bottom"],[194,150,"right",816.66,346,"bottom"],[194,30,"right",1506.66,596,"bottom"],[194,90,"right",1475,346,"bottom"],[194,60,"right",126.66,846,"bottom"],[654,296,"right",1522.5,346,"bottom"],[1574,546,"right",737.5,596,"bottom"],[194,546,"right",1245,96,"bottom"],[424,796,"right",832.5,596,"bottom"]]};
const STAR20 = {"boxes":[[0,690,190,210],[0,230,190,210],[0,460,190,210],[0,0,190,210],[690,112,190,92],[690,224,190,92],[480,112,190,92],[270,448,190,92],[900,224,190,92],[270,112,190,92],[480,224,190,92],[900,0,190,92],[690,448,190,92],[480,448,190,92],[270,224,190,92],[480,336,190,92],[690,336,190,92],[270,0,190,92],[900,112,190,92],[900,336,190,92],[690,0,190,92],[270,336,190,92],[1170,560,190,92],[480,0,190,92],[1170,224,190,92],[1170,112,190,92],[1170,336,190,92],[1170,448,190,92],[1170,672,190,92],[1170,0,190,92]],"lines":[[194,780,"right",753.33,208,"bottom"],[194,720,"right",333.33,544,"bottom"],[194,750,"right",527.5,320,"bottom"],[194,810,"right",543.33,544,"bottom"],[194,840,"right",753.33,432,"bottom"],[194,870,"right",963.33,432,"bottom"],[194,320,"right",995,320,"bottom"],[194,380,"right",995,96,"bottom"],[194,410,"right",365,320,"bottom"],[194,260,"right",365,96,"bottom"],[194,290,"right",785,96,"bottom"],[194,350,"right",785,320,"bottom"],[194,610,"right",785,544,"bottom"],[194,490,"right",575,432,"bottom"],[194,520,"right",995,208,"bottom"],[194,640,"right",365,432,"bottom"],[194,550,"right",575,208,"bottom"],[194,580,"right",365,208,"bottom"],[194,120,"right",816.66,432,"bottom"],[194,180,"right",1026.66,432,"bottom"],[194,150,"right",816.66,208,"bottom"],[194,30,"right",396.66,544,"bottom"],[194,90,"right",575,320,"bottom"],[194,60,"right",606.66,544,"bottom"],[674,158,"right",622.5,320,"bottom"],[464,494,"right",527.5,432,"bottom"],[884,270,"right",575,96,"bottom"],[884,494,"right",622.5,432,"bottom"]]};
for (const [name, fix] of [["the assistant's plain grid, 40 px apart", GRID40], ["the assistant's star grid, 20 px apart", STAR20]]) {
  ok(`30 tables in ${name}: every line routed round the cards, none on top of another (the live page's geometry)`, () => {
    const { boxes, lines } = unpack(fix);
    const r = routeAll(lines, boxes);
    assert.deepEqual(lines.filter((l) => !r.get(l.id).pts).map((l) => l.id), [], 'lines with no route');
    assert.deepEqual(lines.flatMap((l) => through(r.get(l.id).pts, boxes).map((b) => `${l.id} through ${b}`)), [], 'lines through a card');
    assert.deepEqual(onTop(r, lines), [], 'lines drawn on top of each other');
  });
}

const OPEN150 = {"boxes":[[0,202,190,303],[0,937,190,327],[0,1735,190,327],[0,2533,190,327],[0,3331,190,327],[0,3730,190,303],[0,4504,190,327],[0,4903,190,327],[0,5302,190,327],[0,5701,190,327],[0,6100,190,303],[0,4105,190,327],[0,2932,190,327],[0,2134,190,327],[0,1336,190,327],[286,0,480,173],[862,0,190,238],[8584,0,190,288],[1434,0,190,327],[11408,0,190,327],[1720,0,190,327],[8870,0,190,327],[2006,0,190,190],[2292,0,190,238],[0,577,190,288],[9156,0,190,327],[2578,0,190,327],[9442,0,190,327],[2864,0,190,327],[11408,399,190,189],[1148,0,190,238],[9728,0,190,288],[10014,0,190,327],[10300,0,190,327],[11408,661,190,327],[10586,0,190,327],[10872,0,190,190],[3150,0,190,238],[11158,0,190,288],[11408,1060,190,327],[286,283,190,327],[862,349,190,327],[1148,349,190,327],[1434,437,190,190],[1720,437,190,239],[2006,300,190,288],[2292,349,190,327],[2578,437,190,327],[2864,437,190,327],[3150,349,190,327],[3436,349,190,189],[3436,0,190,238],[3722,349,190,288],[4008,349,190,327],[4294,349,190,327],[4580,349,190,327],[4866,349,190,327],[5152,349,190,189],[3722,0,190,238],[5438,349,190,288],[5724,349,190,327],[6010,300,190,327],[6296,349,190,327],[6582,398,190,327],[6868,437,190,190],[4008,0,190,238],[7154,437,190,288],[7440,349,190,327],[7726,437,190,327],[8012,437,190,327],[8298,300,190,327],[8584,398,190,190],[4294,0,190,238],[8870,437,190,288],[9156,437,190,327],[9442,437,190,327],[9728,398,190,327],[10014,437,190,327],[10300,437,190,190],[4580,0,190,238],[10586,437,190,288],[10872,300,190,327],[11158,398,190,327],[286,720,190,327],[862,786,190,327],[1148,786,190,190],[4866,0,190,238],[1434,737,190,288],[1720,786,190,327],[2006,698,190,327],[2292,786,190,327],[2578,874,190,327],[2864,874,190,190],[5152,0,190,238],[3150,786,190,288],[3436,649,190,327],[3722,747,190,327],[4008,786,190,327],[4294,786,190,327],[4580,786,190,190],[5438,0,190,238],[4866,786,190,288],[5152,649,190,327],[5438,747,190,327],[5724,786,190,327],[6010,737,190,327],[6296,786,190,190],[5724,0,190,238],[6582,835,190,287],[6868,737,190,327],[7154,835,190,327],[7440,786,190,327],[7726,874,190,327],[6010,0,190,190],[6296,0,190,238],[6582,0,190,288],[8012,874,190,327],[6868,0,190,327],[8298,737,190,327],[7154,0,190,327],[8584,698,190,189],[7440,0,190,238],[8870,835,190,287],[7726,0,190,327],[9156,874,190,327],[8012,0,190,327],[11408,1459,190,327],[8298,0,190,190],[11408,1858,190,156],[11408,2086,190,180],[11408,2339,190,205],[11408,2616,190,229],[11408,2918,190,156],[11408,3146,190,180],[11408,3399,190,205],[11408,3676,190,229],[11408,3978,190,156],[11408,4206,190,181],[11408,4459,190,205],[11408,4736,190,230],[11408,5038,190,156],[11408,5266,190,181],[11408,5519,190,205],[11408,5796,190,230],[11408,6098,190,156],[11408,6326,190,181],[11408,6579,190,205],[11408,6856,190,230],[11408,7158,190,156],[11408,7386,190,181]],"lines":[[194,232.3,"right",316,177,"bottom"],[194,262.59,"right",925.33,242.5,"bottom"],[194,414.09,"right",8965,331,"bottom"],[194,323.19,"right",2673,331,"bottom"],[194,444.39,"right",9823,291.5,"bottom"],[194,474.69,"right",10967,193.5,"bottom"],[194,292.89,"right",957,680,"bottom"],[194,383.8,"right",2387,680,"bottom"],[194,353.5,"right",3499.33,242.5,"bottom"],[194,969.69,"right",346,177,"bottom"],[194,1035.09,"right",2355.33,242.5,"bottom"],[194,1067.8,"right",2959,331,"bottom"],[194,1198.59,"right",10395,331,"bottom"],[194,1231.3,"right",11253,291.5,"bottom"],[194,1002.39,"right",1529,630.5,"bottom"],[194,1133.19,"right",2959,768,"bottom"],[194,1165.89,"right",4103,680,"bottom"],[194,1100.5,"right",3785.33,242.5,"bottom"],[194,1800.39,"right",406,177,"bottom"],[194,1833.09,"right",1211.33,242.5,"bottom"],[194,2029.3,"right",10681,331,"bottom"],[194,1767.69,"right",381,614,"bottom"],[194,1865.8,"right",2101,591.5,"bottom"],[194,1898.5,"right",3531,542.5,"bottom"],[194,1963.89,"right",4675,680,"bottom"],[194,1996.59,"right",5819,680,"bottom"],[194,1931.19,"right",4071.33,242.5,"bottom"],[194,2565.69,"right",466,177,"bottom"],[194,2663.8,"right",3276.66,242.5,"bottom"],[194,2598.39,"right",1243,680,"bottom"],[194,2631.09,"right",2673,768,"bottom"],[194,2696.5,"right",3817,640.5,"bottom"],[194,2761.89,"right",5247,542.5,"bottom"],[194,2794.59,"right",6391,680,"bottom"],[194,2827.3,"right",7535,680,"bottom"],[194,2729.19,"right",4357.33,242.5,"bottom"],[194,3363.69,"right",526,177,"bottom"],[194,3396.39,"right",1815,679.5,"bottom"],[194,3429.09,"right",3245,680,"bottom"],[194,3494.5,"right",4389,680,"bottom"],[194,3527.19,"right",5533,640.5,"bottom"],[194,3559.89,"right",6963,630.5,"bottom"],[194,3592.59,"right",8107,768,"bottom"],[194,3625.3,"right",9251,768,"bottom"],[194,3461.8,"right",4643.33,242.5,"bottom"],[194,3760.3,"right",556,177,"bottom"],[194,3790.59,"right",3562.66,242.5,"bottom"],[194,3851.19,"right",4961,680,"bottom"],[194,3881.5,"right",6105,631,"bottom"],[194,3911.8,"right",7249,728.5,"bottom"],[194,3942.09,"right",8679,591.5,"bottom"],[194,3972.39,"right",9823,729,"bottom"],[194,4002.69,"right",10967,631,"bottom"],[194,3820.89,"right",4929.33,242.5,"bottom"],[194,4569.39,"right",616,177,"bottom"],[194,4634.8,"right",3848.66,242.5,"bottom"],[194,4700.19,"right",6677,729,"bottom"],[194,4732.89,"right",7821,768,"bottom"],[194,4765.59,"right",8965,728.5,"bottom"],[194,4798.3,"right",10395,630.5,"bottom"],[194,4536.69,"right",381,1051,"bottom"],[194,4602.09,"right",1815,1117,"bottom"],[194,4667.5,"right",5278.66,242.5,"bottom"],[194,4935.69,"right",646,177,"bottom"],[194,5066.5,"right",4134.66,242.5,"bottom"],[194,5131.89,"right",8393,631,"bottom"],[194,5164.59,"right",9537,768,"bottom"],[194,5197.3,"right",10681,728.5,"bottom"],[194,4968.39,"right",1243,979.5,"bottom"],[194,5001.09,"right",2387,1117,"bottom"],[194,5033.8,"right",3531,980,"bottom"],[194,5099.19,"right",5564.66,242.5,"bottom"],[194,5334.69,"right",676,177,"bottom"],[194,5432.8,"right",4420.66,242.5,"bottom"],[194,5563.59,"right",10109,768,"bottom"],[194,5596.3,"right",11253,729,"bottom"],[194,5367.39,"right",1529,1028.5,"bottom"],[194,5400.09,"right",2959,1067.5,"bottom"],[194,5465.5,"right",4103,1117,"bottom"],[194,5498.19,"right",5247,980,"bottom"],[194,5530.89,"right",5850.66,242.5,"bottom"],[194,5733.69,"right",706,177,"bottom"],[194,5864.5,"right",4706.66,242.5,"bottom"],[194,5766.39,"right",957,1117,"bottom"],[194,5799.09,"right",2101,1029,"bottom"],[194,5831.8,"right",3245,1077.5,"bottom"],[194,5897.19,"right",4675,979.5,"bottom"],[194,5929.89,"right",5819,1117,"bottom"],[194,5995.3,"right",6963,1068,"bottom"],[194,5962.59,"right",6422.66,242.5,"bottom"],[194,6130.3,"right",736,177,"bottom"],[194,6221.19,"right",4992.66,242.5,"bottom"],[194,6160.59,"right",2673,1205,"bottom"],[194,6190.89,"right",3817,1078,"bottom"],[194,6251.5,"right",4961,1077.5,"bottom"],[194,6281.8,"right",6391,979.5,"bottom"],[194,6342.39,"right",7535,1117,"bottom"],[194,6372.69,"right",8107,1205,"bottom"],[194,6312.09,"right",7566.66,242.5,"bottom"],[194,4137.69,"right",586,177,"bottom"],[194,4235.8,"right",5215.33,242.5,"bottom"],[194,4203.09,"right",4389,1117,"bottom"],[194,4301.19,"right",5533,1078,"bottom"],[194,4333.89,"right",6677,1126.5,"bottom"],[194,4268.5,"right",6136.66,193.5,"bottom"],[194,4399.3,"right",8393,1068,"bottom"],[194,4366.59,"right",7852.66,331,"bottom"],[194,4170.39,"right",988.66,242.5,"bottom"],[194,2964.69,"right",496,177,"bottom"],[194,3062.8,"right",5501.33,242.5,"bottom"],[194,3128.19,"right",6105,1068,"bottom"],[194,3193.59,"right",7249,1166,"bottom"],[194,3095.5,"right",6708.66,291.5,"bottom"],[194,3226.3,"right",8679,891.5,"bottom"],[194,3160.89,"right",8138.66,331,"bottom"],[194,2997.39,"right",1529,331,"bottom"],[194,3030.09,"right",2418.66,242.5,"bottom"],[194,2166.69,"right",436,177,"bottom"],[194,2264.8,"right",5787.33,242.5,"bottom"],[194,2395.59,"right",7821,1205,"bottom"],[194,2297.5,"right",6994.66,331,"bottom"],[194,2428.3,"right",8965,1126.5,"bottom"],[194,2330.19,"right",8424.66,193.5,"bottom"],[194,2232.09,"right",1815,331,"bottom"],[194,2362.89,"right",9251,331,"bottom"],[194,2199.39,"right",1274.66,242.5,"bottom"],[194,1368.69,"right",376,177,"bottom"],[194,1466.8,"right",6359.33,242.5,"bottom"],[194,1499.5,"right",7280.66,331,"bottom"],[194,1630.3,"right",9251,1205,"bottom"],[194,1532.19,"right",8679,291.5,"bottom"],[194,1401.39,"right",2101,193.5,"bottom"],[194,1564.89,"right",9537,331,"bottom"],[194,1597.59,"right",10109,331,"bottom"],[194,1434.09,"right",3213.33,242.5,"bottom"],[1056,119.25,"right",8361.33,193.5,"bottom"],[1628,163.5,"right",8075.33,331,"bottom"],[1914,163.5,"right",7789.33,331,"bottom"],[2200,94.75,"right",7503.33,242.5,"bottom"],[194,720.75,"right",7217.33,331,"bottom"],[2772,163.5,"right",6931.33,331,"bottom"],[3058,163.5,"right",6645.33,291.5,"bottom"],[1342,119.25,"right",6073.33,193.5,"bottom"]]};
ok("150 tables in the Studio's own bus matrix, every card open: none on top of another (the live page's geometry)", () => {
  const { boxes, lines } = unpack(OPEN150);
  const r = routeAll(lines, boxes);
  assert.deepEqual(lines.filter((l) => !r.get(l.id).pts).map((l) => l.id), [], 'lines with no route');
  assert.deepEqual(onTop(r, lines), [], 'lines drawn on top of each other');
});

console.log(`diagram-route-verify-r2: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
