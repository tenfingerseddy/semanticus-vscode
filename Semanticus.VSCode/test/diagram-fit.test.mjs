// Diagram cards must never cover each other (Kane, 25 Sep). Expanding or collapsing a table card, one or all,
// moves the cards so none overlaps and every card stays fully visible, and a layout change keeps each card's
// expanded or collapsed state. Executes the SAME pure module the webview ships (webview/src/diagramfit.mjs);
// the overlap check below is the test's own, so a module that forgets to check cannot pass by reporting nothing.
// The live proof on the rendered canvas is tools/uishot/diagram-fit-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { reflowCards, changedPositions, toggleExpansion, setExpansionFor, layoutBox, CARD_GAP, filterHeldHeight, recordsHeight, readableFirstView, FIRST_FIT_MIN_ZOOM } from '../webview/src/diagramfit.mjs';

let passed = 0;
const ok = (name, fn) => { fn(); passed++; console.log('  [PASS] ' + name); };

const overlaps = (cards) => {
  const out = [];
  for (let i = 0; i < cards.length; i++) for (let j = i + 1; j < cards.length; j++) {
    const a = cards[i], b = cards[j];
    if (a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h) out.push(`${a.id} / ${b.id}`);
  }
  return out;
};
// Apply a reflow: cards carry their NEW sizes, prev holds the sizes they had when the positions were right.
const run = (cards, prev) => {
  const pos = reflowCards(cards, prev);
  return cards.map((c) => ({ ...c, ...pos[c.id] }));
};
const sizes = (cards) => Object.fromEntries(cards.map((c) => [c.id, { w: c.w, h: c.h }]));
const byId = (cards) => Object.fromEntries(cards.map((c) => [c.id, c]));

// Kane's screenshot: a star schema on the left, and a tall column of eight calculated and field-parameter tables
// stacked on the right with the bus matrix's 72 gap. Collapsed cards are 190 x 92.
const STACK = ['Bridge Steps', 'Metrics', 'Columns', 'Rows', 'Measure Picker', 'Period Picker', 'KPI Targets', 'Scenario'];
const collapsed = [
  { id: 'Sales', x: 0, y: 200, w: 190, h: 92 },
  ...['Customer', 'Date', 'Product', 'Store', 'Geography'].map((id, i) => ({ id, x: 300 + i * 262, y: 0, w: 190, h: 92 })),
  ...STACK.map((id, i) => ({ id, x: 1700, y: i * (92 + 72), w: 190, h: 92 })),
];
const openH = (id) => 150 + (id.length * 13) % 180;   // expanded heights differ per card, like real column lists
const expandedAll = collapsed.map((c) => ({ ...c, h: openH(c.id) }));

ok('expand all: no card covers another, and every card keeps its x', () => {
  const after = run(expandedAll, sizes(collapsed));
  assert.deepEqual(overlaps(after), [], 'expanded cards must not cover the cards below them');
  for (const c of after) assert.equal(c.x, byId(collapsed)[c.id].x, `${c.id} keeps its x`);
});

ok('expand all: the stack keeps its order and its own gap between cards', () => {
  const after = byId(run(expandedAll, sizes(collapsed)));
  for (let i = 1; i < STACK.length; i++) {
    const up = after[STACK[i - 1]], down = after[STACK[i]];
    assert.equal(down.y - (up.y + up.h), 72, `${STACK[i]} sits one gap below ${STACK[i - 1]}`);
  }
});

ok('collapse all after expand all: the gaps close and every card is back where it started', () => {
  const open = run(expandedAll, sizes(collapsed));
  const closed = run(open.map((c) => ({ ...c, h: byId(collapsed)[c.id].h })), sizes(open));
  assert.deepEqual(closed, collapsed);
});

ok('expand one card in the middle of the stack: only the cards below it move, by exactly its growth', () => {
  const grow = 240;
  const cards = collapsed.map((c) => (c.id === 'Rows' ? { ...c, h: c.h + grow } : c));
  const after = byId(run(cards, sizes(collapsed)));
  const at = STACK.indexOf('Rows');
  for (const [i, id] of STACK.entries()) assert.equal(after[id].y, byId(collapsed)[id].y + (i > at ? grow : 0), id);
  for (const id of ['Sales', 'Customer', 'Date', 'Product', 'Store', 'Geography']) assert.deepEqual(after[id], byId(collapsed)[id], `${id} is not in that column and stays put`);
  assert.deepEqual(overlaps(Object.values(after)), []);
  const back = run(Object.values(after).map((c) => ({ ...c, h: byId(collapsed)[c.id].h })), sizes(Object.values(after)));
  assert.deepEqual(back, collapsed, 'collapsing it again closes the gap');
});

ok('a dragged card that sits partly under another is pushed clear, one gap below it', () => {
  const start = [
    { id: 'A', x: 0, y: 0, w: 190, h: 92 },
    { id: 'B', x: 100, y: 60, w: 190, h: 92 },    // dragged half under A
    { id: 'C', x: 600, y: 0, w: 190, h: 92 },
  ];
  const cards = start.map((c) => (c.id === 'A' ? { ...c, h: 300 } : c));
  const after = byId(run(cards, sizes(start)));
  assert.deepEqual(overlaps(Object.values(after)), []);
  assert.equal(after.B.x, 100, 'B keeps its x');
  assert.equal(after.B.y, 300 + CARD_GAP, 'B moves just clear of A, plus the normal gap');
  assert.deepEqual(after.C, start[2], 'C is nowhere near and stays put');
});

ok('a card that gets wider pushes the cards beside it right, and narrowing it brings them back', () => {
  const start = [
    { id: 'A', x: 0, y: 0, w: 190, h: 92 },
    { id: 'B', x: 238, y: 20, w: 190, h: 92 },
    { id: 'C', x: 476, y: 0, w: 190, h: 92 },
    { id: 'D', x: 238, y: 400, w: 190, h: 92 },   // below the band, so it stays put
  ];
  const wide = start.map((c) => (c.id === 'A' ? { ...c, w: 400 } : c));
  const after = byId(run(wide, sizes(start)));
  assert.deepEqual(overlaps(Object.values(after)), []);
  assert.equal(after.B.x, 238 + 210); assert.equal(after.C.x, 476 + 210);
  assert.equal(after.B.y, 20, 'moving sideways never moves a card up or down');
  assert.deepEqual(after.D, start[3]);
  const back = run(Object.values(after).map((c) => ({ ...c, w: byId(start)[c.id].w })), sizes(Object.values(after)));
  assert.deepEqual(back, start);
});

ok('a card with no earlier size (just added) is placed clear of the others, never on top of them', () => {
  const cards = [{ id: 'A', x: 0, y: 0, w: 190, h: 300 }, { id: 'New', x: 20, y: 40, w: 190, h: 92 }];
  const after = run(cards, { A: { w: 190, h: 300 } });
  assert.deepEqual(overlaps(after), []);
});

ok('no size change and no overlap: nothing moves', () => {
  assert.deepEqual(run(collapsed, sizes(collapsed)), collapsed);
});

ok('typing in an open card\'s column filter moves no card: the card keeps its height from before the filter', () => {
  // Metrics is open at its natural 330. The card records that height while its filter is empty, then the person
  // types and the list shrinks to 120. The card renders at max(natural, held), and the reflow must move nothing.
  const open = collapsed.map((c) => (c.id === 'Metrics' ? { ...c, h: 330 } : c));
  const settled = run(open, sizes(collapsed));
  let held;
  if (recordsHeight(true, '')) held = 330;
  for (const q of ['M', 'Me', 'Metric', 'zzz']) {
    assert.equal(recordsHeight(true, q), false, 'a filtered height is never recorded as the card\'s height');
    const natural = q === 'zzz' ? 60 : 120;
    const rendered = Math.max(natural, filterHeldHeight(true, q, held) ?? 0);
    const typed = settled.map((c) => (c.id === 'Metrics' ? { ...c, h: rendered } : c));
    assert.deepEqual(run(typed, sizes(settled)), typed, `typing "${q}" must not move any card`);
  }
  assert.equal(filterHeldHeight(true, '', held), undefined, 'clearing the filter lets the card size itself again');
  assert.equal(filterHeldHeight(false, 'Me', held), undefined, 'a closed card is never held open by a leftover filter');
  assert.equal(recordsHeight(false, ''), false, 'a closed card records nothing');
});

// ---- expansion is kept per table ----
ok('expand all and collapse all change only the shown tables and keep every other table as it was', () => {
  const before = { Hidden: true, Metrics: false };
  const all = setExpansionFor(before, ['Metrics', 'Rows'], true);
  assert.deepEqual(all, { Hidden: true, Metrics: true, Rows: true });
  assert.deepEqual(setExpansionFor(all, ['Metrics', 'Rows'], false), { Hidden: true });
  assert.deepEqual(toggleExpansion({ Rows: true }, 'Rows'), {});
  assert.deepEqual(toggleExpansion({}, 'Rows'), { Rows: true });
});

ok('a layout is computed with the real size of each card, open or closed', () => {
  assert.deepEqual(layoutBox({ w: 190, h: 92 }, { width: 190, height: 330 }), { w: 190, h: 330 });
  assert.deepEqual(layoutBox({ w: 240, h: 120 }, { width: 190, height: 94 }), { w: 240, h: 120 });
  assert.deepEqual(layoutBox({ w: 190, h: 92 }, undefined), { w: 190, h: 92 });
});

// Journey m11 F17: the first frame of a wide diagram in a narrow Studio was a full fit at zoom 0.25.
ok('the first frame keeps card text readable, starting at the top-left when the whole does not fit', () => {
  assert.equal(FIRST_FIT_MIN_ZOOM, 0.85);
  assert.equal(readableFirstView({ x: 0, y: 0, width: 600, height: 300 }, 760, 600, 1, 24), null, 'a readable full fit is kept');
  const wide = readableFirstView({ x: 100, y: 50, width: 2800, height: 400 }, 760, 600, 0.25, 24);
  assert.equal(wide.zoom, 0.85);
  assert.equal(wide.x, 24 - 100 * 0.85, 'too wide at 0.85: starts at the left edge, padded');
  assert.equal(wide.y, (600 - 400 * 0.85) / 2 - 50 * 0.85, 'tall enough: centred up and down');
  const both = readableFirstView({ x: -40, y: -20, width: 2800, height: 2000 }, 760, 600, 0.2, 24);
  assert.deepEqual(both, { x: 24 + 40 * 0.85, y: 24 + 20 * 0.85, zoom: 0.85 }, 'too big both ways: the top-left corner');
  assert.equal(readableFirstView({ x: 0, y: 0, width: 10, height: 10 }, 0, 0, 0.1, 24), null, 'no canvas size yet: fall back to the plain fit');
});
assert.match(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), '..', 'webview/src/diagram.tsx'), 'utf8'), /readableFirstView\(/,
  'the Diagram frames its first open through readableFirstView');

// ---- the canvas uses the module, and a layout change no longer throws the expansion away ----
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const diagram = readFileSync(resolve(root, 'webview/src/diagram.tsx'), 'utf8');
ok('the Diagram wires the reflow and keeps expansion across a layout change', () => {
  assert.match(diagram, /from '\.\/diagramfit\.mjs'/, 'diagram.tsx must use the shared reflow module');
  assert.match(diagram, /reflowCards\(/, 'a card size change must reflow the cards');
  assert.doesNotMatch(diagram, /expanded:\s*\{\}\s*\}\)\)/, 'arranging must not close every card');
  assert.doesNotMatch(diagram, /active\.expanded/, 'expansion is per table, not reset by switching the saved diagram');
  assert.match(diagram, /filterHeldHeight\(/, 'the table card must hold its height while its column filter is active');
});

// UAT-S11-1-004: one drag saved all seven tables' positions. A save carries only the cards that moved.
ok('a save carries only the cards whose position changed', () => {
  const before = { Sales: { x: 360, y: 180 }, Date: { x: 40, y: 40 }, Product: { x: 700, y: 40 } };
  assert.deepEqual(changedPositions(before, { ...before, Sales: { x: 500, y: 240 } }), { Sales: { x: 500, y: 240 } });
  assert.deepEqual(changedPositions(before, { ...before }), {});
  assert.deepEqual(changedPositions(before, { ...before, Store: { x: 0, y: 0 } }), { Store: { x: 0, y: 0 } });
});
ok('a drag saves the dragged nodes, not every node on the canvas', () => {
  assert.match(diagram, /persistPositions\(nodesToPositions\(dragged\)\)/, 'the drop must save only the dragged nodes');
  assert.doesNotMatch(diagram, /persistPositions\(nodesToPositions\(rf\.getNodes\(\)\)\)/, 'the drop must not save every node');
  assert.match(diagram, /persistPositions\(changedPositions\(/, 'the reflow must save only the cards it moved');
});

console.log(`diagram-fit: ${passed} passed`);
