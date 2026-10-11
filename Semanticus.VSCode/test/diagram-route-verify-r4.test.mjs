// verify: round 4 of the m11/diagram-route verification (28 Sep), left failing on purpose for the builder.
// Executes the SAME code the webview ships: makeRoom (webview/src/diagramfit.mjs), which the Diagram runs after the
// person's assistant saves positions, after a card changes size and after a reload.
//
// Round 4 made room for the lines (Kane, 28 Sep) and added keep: a card the person put back with Undo, or one the
// assistant saves with keep=true (the tool's own description invites it), is never moved by makeRoom. But makeRoom
// only pushes cards right and down, each from the cards before it, and it skips a kept card. So a card BEFORE a kept
// card is still pushed on by its own left or upper neighbour, straight into the kept card, and nothing parts them:
// the page leaves two cards on top of each other. In the live page (tools/uishot/diagram-route-verify-r4-drive.mjs
// section 1) the assistant saves its 80-table grid 20 px apart keeping Fact 002: Fact 001 is pushed 8 px onto Fact 002
// and all nine of Fact 001's lines leave from under Fact 002, behind a card (Kane's first ruling). Keeping Fact 004
// instead leaves Fact 003 90 px under it, with the same nine lines behind.
//
// 1. Three cards in a row 20 px apart, the third kept: the second must not be pushed onto it.
// 2. The live 80-table grid 20 px apart (the harness at 1440 x 900: the assistant's positions, each card's drawn size,
//    the lines leaving each side), each card kept in turn: no two cards on top of each other (at head 52 of 80 fail).
import assert from 'node:assert/strict';
import { makeRoom } from '../webview/src/diagramfit.mjs';

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};
const onTop = (cards) => {
  const out = [];
  for (let i = 0; i < cards.length; i++) for (let j = i + 1; j < cards.length; j++) {
    const a = cards[i], b = cards[j];
    const ox = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x), oy = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y);
    if (ox > 0.5 && oy > 0.5) out.push(`${a.id} and ${b.id} overlap ${Math.round(ox)} x ${Math.round(oy)} px`);
  }
  return out;
};
const apply = (cards, to) => cards.map((c) => ({ ...c, ...(to[c.id] ?? {}) }));

ok('three cards in a row 20 px apart, the third kept: the page does not push the second onto it', () => {
  const cards = [{ id: 'A', x: 0, y: 0, w: 190, h: 92 }, { id: 'B', x: 210, y: 0, w: 190, h: 92 }, { id: 'K', x: 420, y: 0, w: 190, h: 92 }];
  const to = makeRoom(cards, {}, 0, new Set(['K']));
  assert.equal(to.K, undefined, 'the kept card stays');
  assert.deepEqual(onTop(apply(cards, to)), [], 'two cards left on top of each other');
});

// [name, x, y, w, h] as the assistant saved them; lines leaving each side [top, right, bottom, left].
const GRID80 = [["Dim 007",0,0,190,92],["Fact 001",210,0,190,300],["Fact 002",420,0,190,300],["Fact 003",630,0,190,300],["Fact 004",840,0,190,300],["Fact 005",1050,0,190,300],["Fact 006",1260,0,190,300],["Fact 007",1470,0,190,300],["Fact 008",1680,0,190,300],["Dim 001",1890,0,190,92],["Dim 002",2100,0,190,92],["Dim 003",2310,0,190,92],["Dim 004",2520,0,190,92],["Dim 005",2730,0,190,92],["Dim 006",0,320,190,92],["Dim 008",210,320,190,92],["Dim 009",420,320,190,92],["Dim 010",630,320,190,92],["Dim 011",840,320,190,92],["Dim 013",1050,320,190,92],["Dim 015",1260,320,190,92],["Dim 016",1470,320,190,92],["Dim 018",1680,320,190,92],["Dim 020",1890,320,190,92],["Dim 021",2100,320,190,92],["Dim 022",2310,320,190,92],["Dim 023",2520,320,190,92],["Dim 025",2730,320,190,92],["Dim 026",0,640,190,92],["Dim 027",210,640,190,92],["Dim 028",420,640,190,92],["Dim 029",630,640,190,92],["Dim 030",840,640,190,92],["Dim 031",1050,640,190,92],["Dim 032",1260,640,190,92],["Dim 033",1470,640,190,92],["Dim 034",1680,640,190,92],["Dim 035",1890,640,190,92],["Dim 036",2100,640,190,92],["Dim 037",2310,640,190,92],["Dim 038",2520,640,190,92],["Dim 039",2730,640,190,92],["Dim 040",0,960,190,92],["Dim 041",210,960,190,92],["Dim 042",420,960,190,92],["Dim 043",630,960,190,92],["Dim 044",840,960,190,92],["Dim 045",1050,960,190,92],["Dim 046",1260,960,190,92],["Dim 047",1470,960,190,92],["Dim 048",1680,960,190,92],["Dim 049",1890,960,190,92],["Dim 050",2100,960,190,92],["Dim 051",2310,960,190,92],["Dim 052",2520,960,190,92],["Dim 053",2730,960,190,92],["Dim 054",0,1280,190,92],["Dim 055",210,1280,190,92],["Dim 056",420,1280,190,92],["Dim 057",630,1280,190,92],["Dim 058",840,1280,190,92],["Dim 059",1050,1280,190,92],["Dim 060",1260,1280,190,92],["Dim 012",1470,1280,190,92],["Dim 014",1680,1280,190,92],["Dim 017",1890,1280,190,92],["Dim 019",2100,1280,190,92],["Dim 024",2310,1280,190,92],["Loose 001",2520,1280,190,92],["Loose 002",2730,1280,190,92],["Loose 003",0,1600,190,92],["Loose 004",210,1600,190,92],["Loose 005",420,1600,190,92],["Loose 006",630,1600,190,92],["Loose 007",840,1600,190,92],["Loose 008",1050,1600,190,92],["Loose 009",1260,1600,190,92],["Loose 010",1470,1600,190,92],["Loose 011",1680,1600,190,92],["Loose 012",1890,1600,190,92]];
const SIDES80 = {"Fact 001":[0,9,0,0],"Dim 001":[0,1,2,0],"Dim 006":[0,0,2,0],"Dim 011":[0,1,2,0],"Dim 016":[0,0,2,0],"Dim 021":[0,0,1,0],"Dim 026":[0,0,1,0],"Dim 031":[0,0,1,0],"Dim 036":[0,0,2,0],"Dim 041":[0,0,2,0],"Fact 002":[0,9,0,0],"Dim 008":[0,0,2,0],"Dim 013":[0,1,2,0],"Dim 018":[0,0,2,0],"Dim 023":[0,0,2,0],"Dim 028":[0,0,1,0],"Dim 033":[0,0,1,0],"Dim 038":[0,0,1,0],"Dim 043":[0,0,2,0],"Dim 048":[0,0,3,0],"Fact 003":[0,9,0,0],"Dim 015":[0,1,2,0],"Dim 020":[0,0,2,0],"Dim 025":[0,0,2,0],"Dim 030":[0,0,2,0],"Dim 035":[0,0,1,0],"Dim 040":[0,0,1,0],"Dim 045":[0,0,1,0],"Dim 050":[0,0,3,0],"Dim 055":[0,0,2,0],"Fact 004":[0,9,0,0],"Dim 022":[0,0,1,0],"Dim 027":[0,0,1,0],"Dim 032":[0,0,1,0],"Dim 037":[0,0,1,0],"Dim 042":[0,0,1,0],"Dim 047":[0,0,1,0],"Dim 052":[0,0,2,0],"Dim 057":[0,0,1,0],"Dim 002":[0,0,1,0],"Fact 005":[0,9,0,0],"Dim 029":[0,0,1,0],"Dim 034":[0,0,1,0],"Dim 039":[0,0,1,0],"Dim 044":[0,0,1,0],"Dim 049":[0,0,1,0],"Dim 054":[0,0,2,0],"Dim 059":[0,0,1,0],"Dim 004":[0,0,1,0],"Dim 009":[0,1,1,0],"Fact 006":[0,9,0,0],"Dim 046":[0,0,2,0],"Dim 051":[0,0,1,0],"Dim 056":[0,0,2,0],"Fact 007":[0,9,0,0],"Dim 053":[0,0,1,0],"Dim 058":[0,0,2,0],"Dim 003":[0,1,1,0],"Fact 008":[0,9,0,0],"Dim 060":[0,0,2,0],"Dim 005":[0,1,1,0],"Dim 010":[0,0,1,0],"Dim 007":[0,1,0,0]};
ok("the assistant's 80-table grid 20 px apart with one card kept (each in turn): no two cards on top of each other", () => {
  const cards = GRID80.map(([id, x, y, w, h]) => ({ id, x, y, w, h }));
  const lines = new Map(cards.map((c) => { const s = SIDES80[c.id] ?? [0, 0, 0, 0]; return [c.id, { top: s[0], right: s[1], bottom: s[2], left: s[3] }]; }));
  assert.deepEqual(onTop(apply(cards, makeRoom(cards, lines))), [], 'with nothing kept the page parts every card (a guard)');
  const bad = [];
  for (const k of cards) {
    const o = onTop(apply(cards, makeRoom(cards, lines, 0, new Set([k.id]))));
    if (o.length) bad.push(`${k.id} kept: ${o[0]}`);
  }
  assert.deepEqual(bad.slice(0, 6), [], `${bad.length} of ${cards.length} kept cards leave two cards on top of each other`);
});

console.log(`diagram-route-verify-r4: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
