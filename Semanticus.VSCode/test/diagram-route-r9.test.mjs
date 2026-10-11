// Review round 9 of m11/diagram-route (29 Sep), Kane's ruling "Slide down": OPENING IS A VIEW, NOT A LAYOUT CHANGE. While any card
// is open the page draws a spread (the cards below an open card slide down by its growth, then apart) worked out from the SAVED
// layout plus the set of open cards; it saves nothing, and closing the last card puts every card back. The behaviour itself lives
// in diagram.tsx, which no node test loads: the drive tools/uishot/diagram-route-r9-drive.mjs proves it on the live page. These
// cases pin the decisions it takes from webview/src/diagramfit.mjs, the SAME module the webview ships.
import assert from 'node:assert/strict';
import * as fit from '../webview/src/diagramfit.mjs';
const { spreadStart, spreadKey, savedAfterDrag, isArrangeChange, isLayoutChange, CARD_GAP } = fit;

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};

// A column of three cards with a closed box of 190 x 92, 48 px apart, and a card beside the first one.
const closed = (id, x, y, over = {}) => ({ id, x, y, w: 190, h: 92, bw: 190, bh: 92, open: false, ...over });
const column = (over = {}) => [closed('A', 0, 0, over.A), closed('B', 0, 140, over.B), closed('C', 0, 280, over.C), closed('D', 400, 0, over.D)];

ok('nothing open, or an open card drawn at its closed box: there is nothing to spread for and every card stays', () => {
  const none = spreadStart(column());
  assert.equal(none.needed, false);
  assert.deepEqual(none.grown.B, { x: 0, y: 140 });
  const same = spreadStart(column({ A: { open: true } }));
  assert.equal(same.needed, false);
});

ok('an open card drawn 200 px taller slides the cards below it in its column down by exactly that, and no other card', () => {
  const r = spreadStart(column({ A: { open: true, h: 292 } }));
  assert.equal(r.needed, true);
  assert.deepEqual(r.grown.A, { x: 0, y: 0 });
  assert.deepEqual(r.grown.B, { x: 0, y: 340 });
  assert.deepEqual(r.grown.C, { x: 0, y: 480 });
  assert.deepEqual(r.grown.D, { x: 400, y: 0 }, 'a card beside it does not move');
});

ok('a card that is closed but draws taller than its box (its text) is not growth', () => {
  const r = spreadStart(column({ B: { h: 110 } }));
  assert.equal(r.needed, false);
  assert.deepEqual(r.grown.C, { x: 0, y: 280 });
});

ok('the spread is computed from the SAVED spots: the same open cards give the same answer whatever was drawn before', () => {
  const a = spreadStart(column({ A: { open: true, h: 292 }, C: { open: true, h: 250 } }));
  const b = spreadStart(column({ C: { open: true, h: 250 }, A: { open: true, h: 292 } }));
  assert.deepEqual(a.grown, b.grown);
});

ok('two open cards stacked: each pushes what is below it, and no card ends up on another', () => {
  const r = spreadStart(column({ A: { open: true, h: 200 }, B: { open: true, h: 200 } }));
  const at = (id) => ({ ...r.grown[id], w: 190, h: id === 'A' || id === 'B' ? 200 : 92 });
  for (const a of ['A', 'B', 'C']) for (const b of ['A', 'B', 'C']) {
    if (a >= b) continue;
    const p = at(a), q = at(b);
    assert.equal(Math.min(p.y + p.h, q.y + q.h) - Math.max(p.y, q.y) > 0 && Math.min(p.x + p.w, q.x + q.w) - Math.max(p.x, q.x) > 0, false, `${a} and ${b} overlap`);
  }
});

ok('a kept card is a wall: an open card above it grows, and the pass moves everything else, keeping the CARD_GAP', () => {
  const r = spreadStart([closed('A', 0, 0, { open: true, h: 292 }), closed('B', 0, 140), closed('K', 0, 500)], new Set(['K']));
  assert.deepEqual(r.grown.K, { x: 0, y: 500 + 200 }, 'a card that grows above a kept one still moves it by that growth (reflowCards)');
  assert.ok(r.grown.B.y >= 292 + CARD_GAP - 0.01 || r.grown.B.y === 340);
});

ok('spreadKey: the same picture gives the same key; another open set, another size or another layout does not', () => {
  const s = (over = {}, extra = '') => spreadKey([{ id: 'A', w: 190, h: 292, open: true, ...over }, { id: 'B', w: 190, h: 92, open: false }], extra);
  assert.equal(s(), s());
  assert.notEqual(s(), s({ open: false }));
  assert.notEqual(s(), s({ h: 300 }));
  assert.notEqual(s(), s({}, 'free'));
  assert.equal(s({ h: 292.04 }), s(), 'a sub-pixel re-measure is the same picture');
});

ok('a drag while spread: the saved spot moves by the distance dragged, the spread\'s own shift stays out', () => {
  // Saved at (100, 100), drawn at (100, 340) because a card above it opened; the person drags it 30 right and 20 up.
  assert.deepEqual(savedAfterDrag({ x: 100, y: 100 }, { x: 100, y: 340 }, { x: 130, y: 320 }), { x: 130, y: 80 });
  assert.deepEqual(savedAfterDrag({ x: 5, y: 7 }, { x: 5, y: 7 }, { x: 5, y: 7 }), { x: 5, y: 7 }, 'a card that did not move stays');
});

const view = { graph: {}, activeId: 'all', membershipKey: 'ALL#111', layout: 'busmatrix' };
ok('the first rebuild of a page, another diagram, other tables and another layout mode ask for the make-room at once', () => {
  assert.equal(isArrangeChange(null, view), true);
  assert.equal(isArrangeChange(view, { ...view, activeId: 'd2' }), true);
  assert.equal(isArrangeChange(view, { ...view, membershipKey: 'ALL#101' }), true);
  assert.equal(isArrangeChange(view, { ...view, layout: 'hierarchy' }), true);
});
ok('a reloaded model alone (a link) is a layout change whose make-room waits for the spread to end', () => {
  const later = { ...view, graph: {} };
  assert.equal(isLayoutChange(view, later), true);
  assert.equal(isArrangeChange(view, later), false);
});
ok('a rebuild that only shows or hides columns is neither', () => {
  assert.equal(isLayoutChange(view, { ...view }), false);
  assert.equal(isArrangeChange(view, { ...view }), false);
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
