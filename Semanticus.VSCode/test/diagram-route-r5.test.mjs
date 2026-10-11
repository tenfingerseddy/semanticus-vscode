// Review round 5 of m11/diagram-route (29 Sep). Executes the SAME modules the webview ships: makeRoom, reflowCards,
// roomSteps and clearDropped (webview/src/diagramfit.mjs), and routeAll, routeSteps and demandSteps
// (webview/src/diagramroute.mjs). The round 4 verifier's own proofs are test/diagram-route-verify-r4.test.mjs and
// tools/uishot/diagram-route-verify-r4-drive.mjs; these add what they do not pin.
//
// 1. A kept card is a wall. A card pushed toward kept cards goes on past all of them, never onto one; a card lying on a
//    kept card leaves it by the shorter way; a card beside a kept card that nothing pushed stays; reflowCards' overlap
//    pass sends a card pushed down onto a kept card on below it.
// 2. Making room runs once and the same way every time (roomSteps). A layout with room is left alone at once, with no
//    routing at all. The builder's hub-less 150-table model as the live page draws it, in the assistant's plain grid
//    30 px apart and star grid 20 px apart, in whole px at a square window (as an assistant saves them; the round 4
//    verifier's drive section 2): after roomSteps and ONE routing pass, no two lines within 1.5 px along each other for
//    over 8 px, none within 0.5 px of a card's border for over 8 px, none behind a card, every line routed; a second run
//    gives the very same positions. In the plain grid, makeRoom alone (what round 4 drew when its extra rounds did not
//    run) leaves a line on a card's border.
// 3. The work yields: routeSteps and roomSteps are generators that give the page its turn between searches, no step
//    takes long, and running routeSteps to the end gives exactly routeAll's routes.
// 4. A dropped card: the nearest clear spot may be round a corner, and a spot the person can see wins over a nearer one
//    off screen.
// 5. The drop checks where the drag let the card go, not the flow store's copy (a quick drag or a Snap step left the
//    store behind, and the card was saved on another; the drive's sections 4 and 5 prove the behaviour live).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { makeRoom, reflowCards, roomSteps, clearDropped, roomBetween, CARD_GAP } from '../webview/src/diagramfit.mjs';
import { routeAll, routeSteps, drain } from '../webview/src/diagramroute.mjs';

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};
const overlaps = (a, b) => Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x) > 0.5 && Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y) > 0.5;
const apply = (cards, to) => cards.map((c) => ({ ...c, ...(to[c.id] ?? {}) }));
const card = (id, x, y, w = 190, h = 92) => ({ id, x, y, w, h });

// ---- 1. a kept card is a wall ----
ok('a card pushed toward two kept cards in a row goes on past both, never onto either, and they stay', () => {
  const cards = [card('A', 0, 0), card('B', 210, 0), card('K1', 420, 0), card('K2', 658, 0), card('C', 1000, 0)];
  const to = makeRoom(cards, {}, 0, new Set(['K1', 'K2']));
  assert.equal(to.K1, undefined); assert.equal(to.K2, undefined);
  const out = apply(cards, to), by = new Map(out.map((c) => [c.id, c]));
  for (const k of ['K1', 'K2']) assert.ok(!overlaps(by.get('B'), by.get(k)), `B on ${k}`);
  assert.ok(by.get('B').x >= by.get('K2').x + 190 + roomBetween(0, 0) - 0.01, `B went on past both kept cards: at ${by.get('B').x}`);
  for (let i = 0; i < out.length; i++) for (let j = i + 1; j < out.length; j++) assert.ok(!overlaps(out[i], out[j]), `${out[i].id} on ${out[j].id}`);
});
ok('a card lying on a kept card leaves it by the shorter way, and the kept card stays', () => {
  // X lies 50 px down over K: under it is 92 - 50 + 48 = 90 px away, beside it 190 + 48 = 238 px.
  const cards = [card('K', 0, 0), card('X', 0, 50)];
  const to = makeRoom(cards, {}, 0, new Set(['K']));
  assert.equal(to.K, undefined, 'the kept card stays');
  assert.deepEqual(to.X, { x: 0, y: 92 + roomBetween(0, 0) }, 'X goes just under K');
});
ok('a card beside a kept card that nothing pushed stays where it is', () => {
  const cards = [card('A', 0, 0), card('K', 0, 96)];
  assert.deepEqual(makeRoom(cards, {}, 0, new Set(['K'])), {}, 'A is not moved for a kept card it does not lie on');
});
ok("reflowCards: a card pushed down onto a kept card goes on below it", () => {
  // B lies half on A and is pushed down clear of it, to 92 + 48 = 140, where the kept K sits (from 150).
  const cards = [card('A', 0, 0), card('B', 0, 50), card('K', 0, 150)];
  const out = reflowCards(cards, {}, CARD_GAP, new Set(['K']));
  assert.equal(out.K.y, 150, 'the kept card stays');
  assert.ok(!overlaps({ ...cards[1], ...out.B }, { ...cards[2], ...out.K }), `B at ${out.B.y} lies on K at 150`);
  assert.equal(out.B.y, 150 + 92 + CARD_GAP, 'B goes on just below K');
});

// ---- a small model in the assistant's star grid (the yielding check), and the measures ----
// big() of tools/uishot/diagram-route-verify-r4-drive.mjs: facts with perFact lookup tables each, a few snowflakes,
// unlinked tables. Each fact's lines leave its right side, each lookup table's its bottom, spread along
// the side in the order of the card at the other end (computeAnchors' angle sort, near enough).
function big(nFacts, nDims, nIso, perFact) {
  const pad = (i) => String(i + 1).padStart(3, '0');
  const facts = Array.from({ length: nFacts }, (_, i) => 'Fact ' + pad(i)), dims = Array.from({ length: nDims }, (_, i) => 'Dim ' + pad(i));
  const iso = Array.from({ length: nIso }, (_, i) => 'Loose ' + pad(i));
  const rels = [], seen = new Set();
  const add = (a, b) => { const k = a + '_' + b; if (a !== b && !seen.has(k)) { seen.add(k); rels.push([a, b]); } };
  facts.forEach((f, fi) => { for (let k = 0; k < perFact; k++) add(f, dims[(fi * 7 + k * 5) % nDims]); });
  for (let i = 0; i + 1 < Math.min(nDims, 16); i += 2) add(dims[i], dims[nDims - 1 - i]);
  return { facts, dims, iso, rels };
}
function starGrid(m, gap) {
  const cols = Math.max(1, Math.round(Math.sqrt(m.dims.length * 92 / 190)));
  const out = []; let y = 0;
  for (const f of m.facts) { out.push(card(f, 0, y, 190, 300)); y += 300 + gap; }
  const x0 = 190 + gap + 60;
  m.dims.forEach((d, i) => out.push(card(d, x0 + (i % cols) * (190 + gap), Math.floor(i / cols) * (92 + gap))));
  const lx = x0 + cols * (190 + gap) + 60;
  m.iso.forEach((t, i) => out.push(card(t, lx + Math.floor(i / 12) * (190 + gap), (i % 12) * (92 + gap))));
  return out;
}
// The links roomSteps takes and the per-side counts makeRoom takes, from where the cards are.
function linksOf(m, cards) {
  const by = new Map(cards.map((c) => [c.id, c]));
  const sideOf = (t) => (m.facts.includes(t) ? 'right' : 'bottom');
  const ends = [];
  m.rels.forEach(([a, b], i) => { ends.push({ i, t: a, o: b, kind: 'from' }); ends.push({ i, t: b, o: a, kind: 'to' }); });
  const groups = new Map();
  for (const e of ends) { const k = e.t + '|' + sideOf(e.t); if (!groups.has(k)) groups.set(k, []); groups.get(k).push(e); }
  const at = new Map();
  for (const g of groups.values()) {
    const s = sideOf(g[0].t), c = by.get(g[0].t);
    g.sort((p, q) => { const P = by.get(p.o), Q = by.get(q.o); return s === 'right' ? (P.y - c.y) - (Q.y - c.y) || P.x - Q.x : (P.x - c.x) - (Q.x - c.x) || P.y - Q.y; });
    g.forEach((e, j) => at.set(e.i + e.kind, { id: e.t, side: s, frac: (j + 1) / (g.length + 1) }));
  }
  const links = m.rels.map(([a, b], i) => ({ id: a + '_' + b, from: at.get(i + 'from'), to: at.get(i + 'to') }));
  const counts = new Map(cards.map((c) => [c.id, { top: 0, right: 0, bottom: 0, left: 0 }]));
  for (const l of links) { counts.get(l.from.id)[l.from.side]++; counts.get(l.to.id)[l.to.side]++; }
  return { links, counts };
}
const endOn = (c, e) => (e.side === 'right' ? { x: c.x + c.w + 4, y: c.y + e.frac * c.h, side: 'right' } : { x: c.x + e.frac * c.w, y: c.y + c.h + 4, side: 'bottom' });
function routed(cards, links) {
  const by = new Map(cards.map((c) => [c.id, c]));
  const lines = links.map((l) => ({ id: l.id, src: endOn(by.get(l.from.id), l.from), dst: endOn(by.get(l.to.id), l.to) }));
  return { lines, routes: routeAll(lines, cards) };
}
// The round 4 verifier's measures: pairs within 1.5 px along each other for over 8 px, runs within 0.5 px of the
// border of a card the line does not join for over 8 px, runs through a card, and lines with no route.
function faults(cards, lines, routes) {
  const segs = [], out = { pairs: [], borders: [], behind: [], unrouted: [] };
  for (const l of lines) { const p = routes.get(l.id)?.pts; if (!p) { out.unrouted.push(l.id); continue; } for (let i = 1; i < p.length; i++) segs.push({ id: l.id, a: p[i - 1], b: p[i] }); }
  const run = new Map();
  for (let i = 0; i < segs.length; i++) for (let j = i + 1; j < segs.length; j++) {
    const s = segs[i], t = segs[j]; if (s.id === t.id) continue;
    const v = Math.abs(s.a.x - s.b.x) < 0.01 && Math.abs(t.a.x - t.b.x) < 0.01, h = Math.abs(s.a.y - s.b.y) < 0.01 && Math.abs(t.a.y - t.b.y) < 0.01;
    if (!v && !h) continue;
    if ((v ? Math.abs(s.a.x - t.a.x) : Math.abs(s.a.y - t.a.y)) >= 1.5) continue;
    const q = v ? 'y' : 'x';
    const ov = Math.min(Math.max(s.a[q], s.b[q]), Math.max(t.a[q], t.b[q])) - Math.max(Math.min(s.a[q], s.b[q]), Math.min(t.a[q], t.b[q]));
    if (ov > 0) { const k = s.id + ' + ' + t.id; run.set(k, (run.get(k) ?? 0) + ov); }
  }
  for (const [k, len] of run) if (len > 8) out.pairs.push(`${k}: ${Math.round(len)} px`);
  for (const s of segs) {
    const [from, to] = s.id.split('_');
    const x0 = Math.min(s.a.x, s.b.x), x1 = Math.max(s.a.x, s.b.x), y0 = Math.min(s.a.y, s.b.y), y1 = Math.max(s.a.y, s.b.y), v = x1 - x0 < 0.01;
    for (const c of cards) {
      if (x0 < c.x + c.w - 0.5 && x1 > c.x + 0.5 && y0 < c.y + c.h - 0.5 && y1 > c.y + 0.5) out.behind.push(`${s.id} behind ${c.id}`);
      if (c.id === from || c.id === to || (!v && y1 - y0 >= 0.01)) continue;
      const at = v ? x0 : y0, e0 = v ? c.x : c.y, e1 = e0 + (v ? c.w : c.h);
      const ov = Math.min(v ? y1 : x1, v ? c.y + c.h : c.x + c.w) - Math.max(v ? y0 : x0, v ? c.y : c.x);
      if ((Math.abs(at - e0) < 0.5 || Math.abs(at - e1) < 0.5) && ov > 8) out.borders.push(`${s.id} along ${c.id} for ${Math.round(ov)} px`);
    }
  }
  return out;
}

// ---- 2. making room runs once, the same way every time ----
ok('a layout with room is left alone at once: roomSteps ends on its first step, with no routing', () => {
  const cards = [card('A', 0, 0), card('B', 300, 0), card('C', 0, 200)];
  const job = roomSteps(cards, [{ id: 'A_B', from: { id: 'A', side: 'right', frac: 0.5 }, to: { id: 'B', side: 'bottom', frac: 0.5 } }], new Map());
  const first = job.next();
  assert.equal(first.done, true, 'it did work it did not need');
  assert.deepEqual(first.value, {});
});
// The builder's hub-less 150-table model as the live page draws it (the round 4 verifier's harness at 900 x 900):
// [id, w, h] per card in whole px, and each line as [from, side, frac, to, side, frac] (r right, b bottom).
const LIVE150 = {"cards":[["Fact 001",190,300],["Fact 002",190,300],["Fact 003",190,300],["Fact 004",190,300],["Fact 005",190,300],["Fact 006",190,300],["Fact 007",190,300],["Fact 008",190,300],["Fact 009",190,300],["Fact 010",190,300],["Fact 011",190,300],["Fact 012",190,300],["Fact 013",190,300],["Fact 014",190,300],["Fact 015",190,300],["Dim 001",190,92],["Dim 002",190,92],["Dim 003",190,92],["Dim 004",190,92],["Dim 005",190,92],["Dim 006",190,92],["Dim 007",190,92],["Dim 008",190,92],["Dim 009",190,92],["Dim 010",190,92],["Dim 011",190,92],["Dim 012",190,92],["Dim 013",190,92],["Dim 014",190,92],["Dim 015",190,92],["Dim 016",190,92],["Dim 017",190,92],["Dim 018",190,92],["Dim 019",190,92],["Dim 020",190,92],["Dim 021",190,92],["Dim 022",190,92],["Dim 023",190,92],["Dim 024",190,92],["Dim 025",190,92],["Dim 026",190,92],["Dim 027",190,92],["Dim 028",190,92],["Dim 029",190,92],["Dim 030",190,92],["Dim 031",190,92],["Dim 032",190,92],["Dim 033",190,92],["Dim 034",190,92],["Dim 035",190,92],["Dim 036",190,92],["Dim 037",190,92],["Dim 038",190,92],["Dim 039",190,92],["Dim 040",190,92],["Dim 041",190,92],["Dim 042",190,92],["Dim 043",190,92],["Dim 044",190,92],["Dim 045",190,92],["Dim 046",190,92],["Dim 047",190,92],["Dim 048",190,92],["Dim 049",190,92],["Dim 050",190,92],["Dim 051",190,92],["Dim 052",190,92],["Dim 053",190,92],["Dim 054",190,92],["Dim 055",190,92],["Dim 056",190,92],["Dim 057",190,92],["Dim 058",190,92],["Dim 059",190,92],["Dim 060",190,92],["Dim 061",190,92],["Dim 062",190,92],["Dim 063",190,92],["Dim 064",190,92],["Dim 065",190,92],["Dim 066",190,92],["Dim 067",190,92],["Dim 068",190,92],["Dim 069",190,92],["Dim 070",190,92],["Dim 071",190,92],["Dim 072",190,92],["Dim 073",190,92],["Dim 074",190,92],["Dim 075",190,92],["Dim 076",190,92],["Dim 077",190,92],["Dim 078",190,92],["Dim 079",190,92],["Dim 080",190,92],["Dim 081",190,92],["Dim 082",190,92],["Dim 083",190,92],["Dim 084",190,92],["Dim 085",190,92],["Dim 086",190,92],["Dim 087",190,92],["Dim 088",190,92],["Dim 089",190,92],["Dim 090",190,92],["Dim 091",190,92],["Dim 092",190,92],["Dim 093",190,92],["Dim 094",190,92],["Dim 095",190,92],["Dim 096",190,92],["Dim 097",190,92],["Dim 098",190,92],["Dim 099",190,92],["Dim 100",190,92],["Dim 101",190,92],["Dim 102",190,92],["Dim 103",190,92],["Dim 104",190,92],["Dim 105",190,92],["Dim 106",190,92],["Dim 107",190,92],["Dim 108",190,92],["Dim 109",190,92],["Dim 110",190,92],["Dim 111",190,92],["Dim 112",190,92],["Dim 113",190,92],["Loose 001",190,92],["Loose 002",190,92],["Loose 003",190,92],["Loose 004",190,92],["Loose 005",190,92],["Loose 006",190,92],["Loose 007",190,92],["Loose 008",190,92],["Loose 009",190,92],["Loose 010",190,92],["Loose 011",190,92],["Loose 012",190,92],["Loose 013",190,92],["Loose 014",190,92],["Loose 015",190,92],["Loose 016",190,92],["Loose 017",190,92],["Loose 018",190,92],["Loose 019",190,92],["Loose 020",190,92],["Loose 021",190,92],["Loose 022",190,92]],"links":[["Fact 001","r",0.1,"Dim 001","b",0.6666],["Fact 001","r",0.3,"Dim 006","b",0.6666],["Fact 001","r",0.2,"Dim 011","b",0.6666],["Fact 001","r",0.4,"Dim 016","b",0.6666],["Fact 001","r",0.5,"Dim 021","b",0.6666],["Fact 001","r",0.6,"Dim 026","b",0.6666],["Fact 001","r",0.9,"Dim 031","b",0.5],["Fact 001","r",0.7,"Dim 036","b",0.3333],["Fact 001","r",0.8,"Dim 041","b",0.3333],["Fact 002","r",0.7,"Dim 008","b",0.5],["Fact 002","r",0.2,"Dim 013","b",0.5],["Fact 002","r",0.8,"Dim 018","b",0.5],["Fact 002","r",0.9,"Dim 023","b",0.5],["Fact 002","r",0.1,"Dim 028","b",0.5],["Fact 002","r",0.3,"Dim 033","b",0.5],["Fact 002","r",0.6,"Dim 038","b",0.5],["Fact 002","r",0.4,"Dim 043","b",0.3333],["Fact 002","r",0.5,"Dim 048","b",0.3333],["Fact 003","r",0.3,"Dim 015","b",0.5],["Fact 003","r",0.9,"Dim 020","b",0.5],["Fact 003","r",0.1,"Dim 025","b",0.5],["Fact 003","r",0.2,"Dim 030","b",0.5],["Fact 003","r",0.4,"Dim 035","b",0.5],["Fact 003","r",0.7,"Dim 040","b",0.5],["Fact 003","r",0.8,"Dim 045","b",0.5],["Fact 003","r",0.5,"Dim 050","b",0.3333],["Fact 003","r",0.6,"Dim 055","b",0.3333],["Fact 004","r",0.8,"Dim 022","b",0.5],["Fact 004","r",0.1,"Dim 027","b",0.5],["Fact 004","r",0.2,"Dim 032","b",0.5],["Fact 004","r",0.3,"Dim 037","b",0.5],["Fact 004","r",0.4,"Dim 042","b",0.5],["Fact 004","r",0.7,"Dim 047","b",0.5],["Fact 004","r",0.9,"Dim 052","b",0.5],["Fact 004","r",0.5,"Dim 057","b",0.3333],["Fact 004","r",0.6,"Dim 062","b",0.3333],["Fact 005","r",0.2,"Dim 029","b",0.5],["Fact 005","r",0.3,"Dim 034","b",0.5],["Fact 005","r",0.4,"Dim 039","b",0.5],["Fact 005","r",0.5,"Dim 044","b",0.5],["Fact 005","r",0.8,"Dim 049","b",0.5],["Fact 005","r",0.9,"Dim 054","b",0.5],["Fact 005","r",0.1,"Dim 059","b",0.5],["Fact 005","r",0.6,"Dim 064","b",0.3333],["Fact 005","r",0.7,"Dim 069","b",0.3333],["Fact 006","r",0.4,"Dim 036","b",0.6666],["Fact 006","r",0.5,"Dim 041","b",0.6666],["Fact 006","r",0.6,"Dim 046","b",0.5],["Fact 006","r",0.9,"Dim 051","b",0.5],["Fact 006","r",0.1,"Dim 056","b",0.5],["Fact 006","r",0.2,"Dim 061","b",0.5],["Fact 006","r",0.3,"Dim 066","b",0.5],["Fact 006","r",0.7,"Dim 071","b",0.3333],["Fact 006","r",0.8,"Dim 076","b",0.3333],["Fact 007","r",0.6,"Dim 043","b",0.6666],["Fact 007","r",0.7,"Dim 048","b",0.6666],["Fact 007","r",0.9,"Dim 053","b",0.5],["Fact 007","r",0.3,"Dim 058","b",0.5],["Fact 007","r",0.4,"Dim 063","b",0.5],["Fact 007","r",0.5,"Dim 068","b",0.5],["Fact 007","r",0.8,"Dim 073","b",0.5],["Fact 007","r",0.1,"Dim 078","b",0.3333],["Fact 007","r",0.2,"Dim 083","b",0.3333],["Fact 008","r",0.6,"Dim 050","b",0.6666],["Fact 008","r",0.8,"Dim 055","b",0.6666],["Fact 008","r",0.3,"Dim 060","b",0.5],["Fact 008","r",0.4,"Dim 065","b",0.5],["Fact 008","r",0.5,"Dim 070","b",0.5],["Fact 008","r",0.7,"Dim 075","b",0.5],["Fact 008","r",0.9,"Dim 080","b",0.5],["Fact 008","r",0.1,"Dim 085","b",0.6666],["Fact 008","r",0.2,"Dim 090","b",0.6666],["Fact 009","r",0.6,"Dim 057","b",0.6666],["Fact 009","r",0.7,"Dim 062","b",0.6666],["Fact 009","r",0.3,"Dim 067","b",0.5],["Fact 009","r",0.4,"Dim 072","b",0.5],["Fact 009","r",0.5,"Dim 077","b",0.5],["Fact 009","r",0.8,"Dim 082","b",0.5],["Fact 009","r",0.9,"Dim 087","b",0.5],["Fact 009","r",0.1,"Dim 092","b",0.6666],["Fact 009","r",0.2,"Dim 097","b",0.6666],["Fact 010","r",0.7,"Dim 064","b",0.6666],["Fact 010","r",0.8,"Dim 069","b",0.6666],["Fact 010","r",0.5,"Dim 074","b",0.5],["Fact 010","r",0.6,"Dim 079","b",0.5],["Fact 010","r",0.9,"Dim 084","b",0.5],["Fact 010","r",0.1,"Dim 089","b",0.5],["Fact 010","r",0.3,"Dim 094","b",0.5],["Fact 010","r",0.2,"Dim 099","b",0.5],["Fact 010","r",0.4,"Dim 104","b",0.6666],["Fact 011","r",0.7,"Dim 071","b",0.6666],["Fact 011","r",0.8,"Dim 076","b",0.6666],["Fact 011","r",0.6,"Dim 081","b",0.5],["Fact 011","r",0.9,"Dim 086","b",0.5],["Fact 011","r",0.1,"Dim 091","b",0.5],["Fact 011","r",0.2,"Dim 096","b",0.5],["Fact 011","r",0.3,"Dim 101","b",0.3333],["Fact 011","r",0.4,"Dim 106","b",0.5],["Fact 011","r",0.5,"Dim 111","b",0.6666],["Fact 012","r",0.1,"Dim 078","b",0.6666],["Fact 012","r",0.2,"Dim 083","b",0.6666],["Fact 012","r",0.9,"Dim 088","b",0.5],["Fact 012","r",0.3,"Dim 093","b",0.5],["Fact 012","r",0.6,"Dim 098","b",0.5],["Fact 012","r",0.5,"Dim 103","b",0.6666],["Fact 012","r",0.7,"Dim 108","b",0.5],["Fact 012","r",0.8,"Dim 113","b",0.6666],["Fact 012","r",0.4,"Dim 005","b",0.5],["Fact 013","r",0.1,"Dim 085","b",0.3333],["Fact 013","r",0.2,"Dim 090","b",0.3333],["Fact 013","r",0.4,"Dim 095","b",0.5],["Fact 013","r",0.5,"Dim 100","b",0.5],["Fact 013","r",0.6,"Dim 105","b",0.6666],["Fact 013","r",0.7,"Dim 110","b",0.5],["Fact 013","r",0.8,"Dim 002","b",0.5],["Fact 013","r",0.3,"Dim 007","b",0.5],["Fact 013","r",0.9,"Dim 012","b",0.5],["Fact 014","r",0.1,"Dim 092","b",0.3333],["Fact 014","r",0.2,"Dim 097","b",0.3333],["Fact 014","r",0.5,"Dim 102","b",0.5],["Fact 014","r",0.4,"Dim 107","b",0.6666],["Fact 014","r",0.7,"Dim 112","b",0.5],["Fact 014","r",0.6,"Dim 004","b",0.5],["Fact 014","r",0.3,"Dim 009","b",0.5],["Fact 014","r",0.8,"Dim 014","b",0.5],["Fact 014","r",0.9,"Dim 019","b",0.5],["Fact 015","r",0.3,"Dim 099","b",0.25],["Fact 015","r",0.8,"Dim 104","b",0.3333],["Fact 015","r",0.9,"Dim 109","b",0.6666],["Fact 015","r",0.1,"Dim 001","b",0.3333],["Fact 015","r",0.4,"Dim 006","b",0.3333],["Fact 015","r",0.2,"Dim 011","b",0.3333],["Fact 015","r",0.5,"Dim 016","b",0.3333],["Fact 015","r",0.6,"Dim 021","b",0.3333],["Fact 015","r",0.7,"Dim 026","b",0.3333],["Dim 001","r",0.5,"Dim 113","b",0.3333],["Dim 003","r",0.5,"Dim 111","b",0.3333],["Dim 005","r",0.5,"Dim 109","b",0.3333],["Dim 007","r",0.5,"Dim 107","b",0.3333],["Dim 009","r",0.5,"Dim 105","b",0.3333],["Dim 011","r",0.5,"Dim 103","b",0.3333],["Dim 013","r",0.5,"Dim 101","b",0.6666],["Dim 015","r",0.5,"Dim 099","b",0.75]]};
const SIDE = { r: 'right', b: 'bottom', l: 'left', t: 'top' };
function live150(kind, gap) {
  const links = LIVE150.links.map(([a, sa, fa, b, sb, fb]) => ({ id: a + '_' + b, from: { id: a, side: SIDE[sa], frac: fa }, to: { id: b, side: SIDE[sb], frac: fb } }));
  const from = new Set(links.map((l) => l.from.id)), to = new Set(links.map((l) => l.to.id));
  const role = (n) => (from.has(n) && !to.has(n) ? 0 : to.has(n) ? 1 : 2);
  const size = new Map(LIVE150.cards.map(([id, w, h]) => [id, { w, h }]));
  const at = {};
  if (kind === 'plain') {   // the assistant's plain grid: facts, then lookup tables, then the rest, by name
    const names = LIVE150.cards.map((c) => c[0]).sort((a, b) => role(a) - role(b) || a.localeCompare(b));
    const cw = Math.max(...LIVE150.cards.map((c) => c[1])), ch = Math.max(...LIVE150.cards.map((c) => c[2]));
    const cols = Math.max(1, Math.round(Math.sqrt(names.length * ch / cw)));
    names.forEach((n, i) => { at[n] = { x: (i % cols) * (cw + gap), y: Math.floor(i / cols) * (ch + gap) }; });
  } else {                  // the assistant's star grid: facts down the left, lookup tables in a grid, the rest beside it
    const ids = LIVE150.cards.map((c) => c[0]), facts = ids.filter((n) => role(n) === 0).sort(), dims = ids.filter((n) => role(n) === 1).sort(), loose = ids.filter((n) => role(n) === 2).sort();
    const fw = Math.max(190, ...facts.map((f) => size.get(f).w)), cw = Math.max(190, ...dims.map((d) => size.get(d).w)), ch = Math.max(92, ...dims.map((d) => size.get(d).h));
    const cols = Math.max(1, Math.round(Math.sqrt(dims.length * ch / cw)));
    let y = 0;
    for (const f of facts) { at[f] = { x: 0, y }; y += size.get(f).h + gap; }
    const x0 = fw + gap + 60;
    dims.forEach((d, i) => { at[d] = { x: x0 + (i % cols) * (cw + gap), y: Math.floor(i / cols) * (ch + gap) }; });
    const lx = x0 + cols * (cw + gap) + 60;
    loose.forEach((t, i) => { at[t] = { x: lx + Math.floor(i / 12) * (cw + gap), y: (i % 12) * (ch + gap) }; });
  }
  const cards = LIVE150.cards.map(([id, w, h]) => card(id, at[id].x, at[id].y, w, h));
  const counts = new Map(cards.map((c) => [c.id, { top: 0, right: 0, bottom: 0, left: 0 }]));
  for (const l of links) { counts.get(l.from.id)[l.from.side]++; counts.get(l.to.id)[l.to.side]++; }
  return { cards, links, counts };
}
for (const [kind, gap, firstRoundFaults] of [['plain', 30, true], ['star', 20, false]]) {
  ok(`the live 150-table model in the assistant's ${kind} grid ${gap} px apart, whole px: room made once, then one routing pass draws no two lines on top, none on a card's border, none behind a card; the same result twice`, () => {
    const { cards, links, counts } = live150(kind, gap);
    // What round 4 drew whenever its extra rounds did not run (review round 4, R4-5): makeRoom alone.
    const one = apply(cards, makeRoom(cards, counts)), r1 = routed(one, links), f1 = faults(one, r1.lines, r1.routes);
    if (firstRoundFaults) assert.ok(f1.borders.length > 0, 'the fixture must leave a line on a border after makeRoom alone');
    const to = drain(roomSteps(cards, links, counts));
    assert.ok(Object.keys(to).length > 0, 'the page makes room');
    const moved = apply(cards, to);
    for (const c of moved) { const o = cards.find((q) => q.id === c.id); assert.ok(c.x >= o.x - 0.01 && c.y >= o.y - 0.01, `${c.id} moved left or up`); }
    const { lines, routes } = routed(moved, links);
    const f = faults(moved, lines, routes);
    assert.deepEqual(f.unrouted.slice(0, 3), [], 'lines with no route');
    assert.deepEqual(f.behind.slice(0, 3), [], 'lines behind a card');
    assert.deepEqual(f.pairs.slice(0, 3), [], 'lines on top of each other');
    assert.deepEqual(f.borders.slice(0, 3), [], "lines on a card's border");
    assert.deepEqual(drain(roomSteps(cards, links, counts)), to, 'a second run ends somewhere else');
  });
}

// ---- 3. the work yields ----
ok('routeSteps run to its end gives exactly routeAll\'s routes, and yields between searches', () => {
  const cards = starGrid(big(4, 24, 4, 6), 20);
  const { links } = linksOf(big(4, 24, 4, 6), cards);
  const { lines, routes } = routed(cards, links);
  const job = routeSteps(lines, cards);
  let steps = 0, r;
  do { r = job.next(); steps++; } while (!r.done);
  assert.ok(steps > lines.length, `only ${steps} steps for ${lines.length} lines`);
  assert.deepEqual([...r.value.keys()], [...routes.keys()]);
  for (const [id, x] of routes) assert.deepEqual(r.value.get(id).pts, x.pts, `${id} routed differently`);
});
ok('roomSteps on the 150-table star grid yields between searches, and no step takes long', () => {
  const { cards, links, counts } = live150('star', 20);
  const job = roomSteps(cards, links, counts);
  let steps = 0, longest = 0, r;
  for (;;) { const t = performance.now(); r = job.next(); longest = Math.max(longest, performance.now() - t); steps++; if (r.done) break; }
  assert.ok(steps > 2 * links.length, `only ${steps} steps for ${links.length} lines`);
  // A step is one search: a few ms. The bound is loose so a loaded machine does not fail it; the page slices at 12 ms.
  assert.ok(longest < 1000, `a step took ${Math.round(longest)} ms`);
  console.log(`    (${steps} steps, the longest ${Math.round(longest)} ms)`);
});

// ---- 4. a dropped card ----
ok('a dropped card goes round a corner when that is the nearest clear spot', () => {
  // X is dropped on A. Straight out every way is blocked close by: a tall card to the right, a wide one under, and
  // cards to the left and above. The nearest spot straight out is past the tall card, 476 px off; beside the wide card
  // and under the tall one is 427 px off, down and to the right.
  const cards = [card('A', 0, 0), card('R', 238, -200, 190, 500), card('D', -200, 140, 400, 300), card('L', -400, -200, 190, 500),
    card('U', -200, -400, 400, 300), card('X', 0, 0)];
  const to = clearDropped(cards, ['X'], CARD_GAP, 0, null).X;
  assert.ok(to, 'X moved');
  const moved = cards.map((c) => (c.id === 'X' ? { ...c, ...to } : c)), x = moved.find((c) => c.id === 'X');
  for (const c of moved) if (c.id !== 'X') assert.ok(!overlaps(x, c), `X on ${c.id}`);
  assert.ok(to.x !== 0 && to.y !== 0, `X went straight out, to (${to.x}, ${to.y})`);
  assert.ok(Math.hypot(to.x, to.y) < 476 - 1, `X slid ${Math.round(Math.hypot(to.x, to.y))} px`);
});
ok('a spot the person can see wins over a nearer one off screen', () => {
  const cards = [card('A', 0, 0), card('B', 238, 0), card('X', 10, 10)];
  const any = clearDropped(cards, ['X'], CARD_GAP, 0, null).X;
  assert.deepEqual(any, { x: 10, y: 140 }, 'with no view, straight down is nearest');
  const view = { x: -400, y: -300, w: 700, h: 400 };   // what is below y = 100 is off screen
  const seen = clearDropped(cards, ['X'], CARD_GAP, 0, null, view).X;
  assert.ok(seen.y + 92 <= view.y + view.h && seen.x >= view.x && seen.x + 190 <= view.x + view.w, `X at (${seen.x}, ${seen.y}) is off screen`);
  const moved = cards.map((c) => (c.id === 'X' ? { ...c, ...seen } : c));
  for (const c of moved) if (c.id !== 'X') assert.ok(!overlaps(moved.find((q) => q.id === 'X'), c), `X on ${c.id}`);
});

// ---- 5. the drop checks the drag's own spot ----
ok("the drop checks where the drag let the card go, not the flow store's copy", () => {
  const src = readFileSync(new URL('../webview/src/diagram.tsx', import.meta.url), 'utf8');
  const body = src.slice(src.indexOf('const commitPositions = useCallback('), src.indexOf('// Keep cards clear of each other whenever one changes size'));
  assert.match(body, /const at = new Map\(dragged\.map\(\(n\) => \[n\.id, n\.position\]\)\)/, 'the dragged nodes\' own positions are read');
  assert.match(body, /rf\.getNodes\(\)\.map\(\(n\) => \(at\.has\(n\.id\)/, 'and laid over the flow store\'s before clearDropped');
});

console.log(`diagram-route-r5: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
