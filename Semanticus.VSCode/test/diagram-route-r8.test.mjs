// Review round 8 of m11/diagram-route (29 Sep), the coordinator's ruling: LOOKING IS NOT A LAYOUT CHANGE. Opening or closing
// a card's columns (one card, Expand all, Collapse all), zooming, panning and selecting never move another card, save or toast.
// An opened card is drawn on top of its neighbours while it is open; the page settles cards by each card's CLOSED box, and
// only a real change of the layout (another diagram, other tables, a layout mode, a reloaded model, a card that needs a bigger
// box for its lines) makes it settle at all. The behaviour itself lives in diagram.tsx, which no node test loads: the drive
// tools/uishot/diagram-route-r8-drive.mjs proves it on the live page. These cases pin the two decisions it takes from
// webview/src/diagramfit.mjs, the SAME module the webview ships.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as fit from '../webview/src/diagramfit.mjs';
const { boxChanges, isLayoutChange, sizesToCompare, layoutBox, reflowCards, CARD_GAP } = fit;
const diagram = readFileSync(new URL('../webview/src/diagram.tsx', import.meta.url), 'utf8');

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};

// A column of three cards, each with a closed box of 190 x 92 and 48 px between them.
const boxes = [{ id: 'A', w: 190, h: 92 }, { id: 'B', w: 190, h: 92 }, { id: 'C', w: 190, h: 92 }];
const last = () => new Map(boxes.map((b) => [b.id, { w: b.w, h: b.h }]));

ok('opening a card changes no box: the drawn (measured) size is not a size change', () => {
  // A opened is drawn 300 px tall; its closed box is still 92.
  const now = boxes.map((b) => (b.id === 'A' ? { ...b, measured: { width: 190, height: 300 }, open: true } : b));
  const { changed, prev } = boxChanges(now, last());
  assert.equal(changed, false);
  assert.deepEqual(prev.A, { w: 190, h: 92 });
});

ok('closing it again changes no box either', () => {
  assert.equal(boxChanges(boxes, last()).changed, false);
});

ok('Expand all and Collapse all change no box', () => {
  const open = boxes.map((b) => ({ ...b, measured: { width: 190, height: 260 }, open: true }));
  assert.equal(boxChanges(open, last()).changed, false);
  assert.equal(boxChanges(boxes, last()).changed, false);
});

ok('a card whose lines need a bigger box IS a size change, and keeps the box it had for the reflow', () => {
  const now = boxes.map((b) => (b.id === 'B' ? { ...b, h: 120 } : b));
  const { changed, prev } = boxChanges(now, last());
  assert.equal(changed, true);
  assert.deepEqual(prev.B, { w: 190, h: 92 });
  // and the cards below it move by exactly that growth, as before (Kane, 25 Sep)
  const at = [{ id: 'A', x: 0, y: 0, ...now[0] }, { id: 'B', x: 0, y: 140, ...now[1] }, { id: 'C', x: 0, y: 280, ...now[2] }];
  assert.deepEqual(reflowCards(at, prev, CARD_GAP).C, { x: 0, y: 308 });
});

ok('the first look has nothing to compare with: no change, no previous boxes', () => {
  const { changed, prev } = boxChanges(boxes, null);
  assert.equal(changed, false);
  assert.deepEqual(prev, {});
});

ok('a card new to the diagram does not count as a size change of the others', () => {
  const { changed, prev } = boxChanges([...boxes, { id: 'D', w: 190, h: 92 }], last());
  assert.equal(changed, false);
  assert.equal('D' in prev, false);
});

// Feeding the DRAWN sizes to the reflow is what made looking move cards: the pass reads an opened card's height as growth.
ok('the drawn size of an opened card WOULD move the cards below it, which is why the page no longer feeds it', () => {
  const at = [{ id: 'A', x: 0, y: 0, w: 190, h: 300 }, { id: 'B', x: 0, y: 140, w: 190, h: 92 }];
  const moved = reflowCards(at, { A: { w: 190, h: 92 }, B: { w: 190, h: 92 } }, CARD_GAP);
  assert.equal(moved.B.y, 348);   // pushed 208 px down by A's columns
});

const view = { graph: {}, activeId: 'all', membershipKey: 'ALL#111', layout: 'busmatrix' };
ok('the first rebuild is a layout change', () => assert.equal(isLayoutChange(null, view), true));
ok('a rebuild for the same graph, diagram, tables and layout (a card opened, its columns arriving) is not', () => {
  assert.equal(isLayoutChange({ ...view }, { ...view }), false);
});
ok('a reloaded model is', () => assert.equal(isLayoutChange(view, { ...view, graph: {} }), true));
ok('another diagram is', () => assert.equal(isLayoutChange(view, { ...view, activeId: 'd2' }), true));
ok('other tables on the diagram are (tables added)', () => assert.equal(isLayoutChange(view, { ...view, membershipKey: 'ALL#101' }), true));
ok('another layout mode is', () => assert.equal(isLayoutChange(view, { ...view, layout: 'hierarchy' }), true));

// Bus matrix after another layout (Kane, 8 Oct: "Bus Matrix view has been broken"). Layered, then Bus matrix, drew a wider and
// ragged grid, unlike a fresh bus matrix. Three things fed the old layout's boxes into the new one, and each is pinned here:
// the arrange read a closed card's size as drawn in the layout before, the settle pass counted that box change as growth a
// second time, and it also ran on the cards of the layout before while the rebuild was still to land.
ok('an arrange into another layout places and settles the cards by their new boxes only', () => {
  assert.deepEqual(layoutBox({ w: 190, h: 92 }, { width: 210, height: 92 }, false), { w: 190, h: 92 });   // Sales as Layered drew it
  assert.deepEqual(layoutBox({ w: 190, h: 92 }, { width: 190, height: 330 }, true), { w: 190, h: 330 });    // an open card still reserves its columns
  assert.match(diagram, /layoutBox\(sizeForAnchors\(anchors\.get\(n\) \?\? EMPTY_ANCHORS\), rf\.getInternalNode\(n\)\?\.measured, !!expanded\[n\]\)/, 'arrangeWith passes whether a card is open');
  const old = new Map([['Sales', { w: 210, h: 92 }], ['Date', { w: 190, h: 92 }]]);
  const placed = [{ id: 'Sales', x: 0, y: 0, w: 190, h: 92 }, { id: 'Date', x: 286, y: 0, w: 190, h: 92 }];   // placed with the bus matrix's boxes
  assert.equal(reflowCards(placed, boxChanges(placed, old).prev, CARD_GAP).Date.x, 266);   // the old comparison pulls Date left by 20 again
  const next = sizesToCompare({ ...view, layout: 'layered' }, view, old);
  assert.equal(reflowCards(placed, boxChanges(placed, next).prev, CARD_GAP).Date.x, 286);
  assert.match(diagram, /nodes\.some\(\(n\) => n\.data\.layout !== layout\)\) return;/, 'the settle pass waits for the rebuild');
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
