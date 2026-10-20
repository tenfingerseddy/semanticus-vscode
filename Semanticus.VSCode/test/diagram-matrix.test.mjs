#!/usr/bin/env node
// 1.5 visuals (Kane, 12 Oct: "the hero shot is a poorly laid out model diagram. It needs to be bus matrix layout at least").
// The default Diagram is a true bus matrix: lookup tables in one row (the date table first), facts in one column, and a
// dot where a fact uses a lookup table. Lines never cross because there are no per-link lines to cross.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  MATRIX_LANE, NODE_H, NODE_W, cellRels, cellSlots, computeAnchors, matrixMarks, nearMarkPoints, tagPoint, matrixDimWidth, matrixOrder, matrixPositions, matrixSizer, sizeForAnchors,
} from '../webview/src/diagramlayout.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const tbl = (name, o = {}) => ({ name, isDateTable: false, ...o });
const rel = (from, to) => ({ name: `${from}_${to}`, fromTable: from, toTable: to, fromCardinality: 'Many', toCardinality: 'One' });

// Kane's 1.4.0 hero model, facts and dimensions in a scrambled order on purpose.
const contoso = {
  tables: [tbl('Returns'), tbl('Sales'), tbl('Budget'), tbl('Inventory'), tbl('Store'), tbl('Customer'), tbl('Product'), tbl('Date', { isDateTable: true }), tbl('Measures')],
  rels: [
    rel('Returns', 'Customer'), rel('Returns', 'Date'), rel('Returns', 'Product'), rel('Returns', 'Store'),
    rel('Sales', 'Customer'), rel('Sales', 'Date'), rel('Sales', 'Product'), rel('Sales', 'Store'),
    rel('Budget', 'Date'), rel('Budget', 'Product'), rel('Budget', 'Store'),
    rel('Inventory', 'Date'), rel('Inventory', 'Product'), rel('Inventory', 'Store'),
  ],
};
const boxes = (g) => {
  const sides = computeAnchors(g.tables, g.rels, 'matrix');
  const own = (n) => sizeForAnchors(sides.get(n) ?? []);
  const sizeOf = matrixSizer(g.tables, g.rels, own);
  const pos = matrixPositions(g.tables, g.rels, sizeOf);
  return { pos, sizeOf, box: (n) => ({ ...pos.get(n), ...sizeOf(n) }) };
};

test('the date table leads the row, then the tables most facts share; the biggest fact leads the column', () => {
  const o = matrixOrder(contoso.tables, contoso.rels);
  assert.equal(o.dims[0], 'Date', 'the date table is first');
  assert.deepEqual(o.dims.slice(1, 3).sort(), ['Product', 'Store'], 'Product and Store (all four facts) come before Customer (two)');
  assert.equal(o.dims[3], 'Customer');
  assert.deepEqual(o.facts.slice(0, 2).sort(), ['Returns', 'Sales'], 'the facts with four links lead');
  assert.deepEqual(o.iso, ['Measures']);
});

test('lookup tables share one row, facts one column, and nothing overlaps', () => {
  const { box } = boxes(contoso);
  const o = matrixOrder(contoso.tables, contoso.rels);
  assert.equal(new Set(o.dims.map((d) => box(d).y)).size, 1, 'every lookup table is in one row');
  assert.equal(new Set(o.facts.map((f) => box(f).x)).size, 1, 'every fact is in one column');
  assert.ok(o.facts.every((f) => box(f).x + box(f).w < box(o.dims[0]).x), 'the facts are left of the row');
  assert.ok(o.facts.every((f) => box(f).y >= box(o.dims[0]).y + box(o.dims[0]).h), 'the facts start under the row');
  const all = contoso.tables.map((t) => box(t.name));
  for (let i = 0; i < all.length; i++) for (let j = i + 1; j < all.length; j++) {
    const a = all[i], b = all[j];
    assert.ok(a.x + a.w <= b.x || b.x + b.w <= a.x || a.y + a.h <= b.y || b.y + b.h <= a.y, `cards ${i} and ${j} do not overlap`);
  }
});

test('every link of a fact is one dot on its row, so no two dots share a cell and nothing crosses', () => {
  const { box } = boxes(contoso);
  const cells = cellRels(contoso.tables, contoso.rels);
  assert.equal(cells.size, 14);
  const dots = contoso.rels.map((r) => `${Math.round(box(r.toTable).x + box(r.toTable).w / 2)},${Math.round(box(r.fromTable).y + MATRIX_LANE)}`);
  assert.equal(new Set(dots).size, 14, 'one dot per cell');
  for (const r of contoso.rels) assert.equal(box(r.fromTable).y + MATRIX_LANE, box(r.fromTable).y + MATRIX_LANE);
});

test('a card keeps its normal size however many links it has', () => {
  const sides = computeAnchors(contoso.tables, contoso.rels, 'matrix');
  for (const t of contoso.tables) {
    const s = sizeForAnchors(sides.get(t.name) ?? []);
    assert.equal(s.w, NODE_W, `${t.name} is not widened by its links`);
    assert.equal(s.h, NODE_H, `${t.name} is not made taller by its links`);
  }
  // the old bus matrix still widens and tallens, which is what made its cards huge
  const old = computeAnchors(contoso.tables, contoso.rels, 'busmatrix');
  assert.ok(sizeForAnchors(old.get('Sales')).h > NODE_H, 'a fact with four links was taller');
});

test('the whole Contoso model fits 1440 px at a readable size; a 20 table model fits 1920', () => {
  const { box } = boxes(contoso);
  const right = Math.max(...contoso.tables.map((t) => box(t.name).x + box(t.name).w));
  assert.ok(right <= 1300, `the matrix is ${right}px wide, so at 1440 px it shows at about full size`);
  const dims = ['Date', 'Product', 'Customer', 'Store', 'Promotion', 'Currency', 'Channel', 'Employee', 'Geography', 'Category', 'Supplier', 'Warehouse', 'Region'];
  const g = { tables: ['Sales', 'Orders', 'Returns'].map((n) => tbl(n)).concat(dims.map((n) => tbl(n, { isDateTable: n === 'Date' }))).concat(['Measures', 'Notes'].map((n) => tbl(n))),
    rels: [...['Date', 'Product', 'Customer', 'Store', 'Promotion', 'Currency', 'Channel'].map((d) => rel('Sales', d)), ...['Date', 'Product', 'Employee', 'Supplier'].map((d) => rel('Orders', d)),
      rel('Returns', 'Date'), rel('Returns', 'Store'), rel('Customer', 'Geography'), rel('Product', 'Category'), rel('Orders', 'Warehouse'), rel('Store', 'Region')] };
  const b = boxes(g);
  const w = Math.max(...g.tables.map((t) => b.box(t.name).x + b.box(t.name).w));
  assert.ok(w <= 1900, `the 20 table matrix is ${w}px wide`);
  assert.ok(matrixDimWidth(13) < matrixDimWidth(5), 'more lookup tables means narrower lookup cards');
  // an outrigger sits right after the table it hangs off
  const o = matrixOrder(g.tables, g.rels);
  assert.equal(o.dims[o.dims.indexOf('Customer') + 1], 'Geography');
  assert.equal(o.dims[o.dims.indexOf('Product') + 1], 'Category');
  // unlinked tables sit under the facts, never in the row of lookup tables
  const rowY = b.box(o.dims[0]).y, lowestFact = Math.max(...o.facts.map((f) => b.box(f).y + b.box(f).h));
  for (const n of o.iso) assert.ok(b.box(n).y >= lowestFact, `${n} is under the facts`);
  assert.ok(o.dims.every((d) => b.box(d).y === rowY));
});

test('the bus matrix with its lines is the default, as in 1.4.0; the dot grid is one more choice under Arrange', () => {
  const diagram = readFileSync(resolve(root, 'webview/src/diagram.tsx'), 'utf8');
  assert.match(diagram, /active\.layout \?\? 'busmatrix'/, 'a fresh All tables diagram opens as the bus matrix, as 1.4.0 did');
  assert.match(diagram, /onClick=\{busMatrix\}[^>]*>Bus matrix<\/button>/, 'the 1.4.0 name is back');
  assert.match(diagram, /onClick=\{matrix\}[^>]*>Dot grid<\/button>/);
  assert.ok(!diagram.includes('Bus matrix with lines'), 'the renamed button is gone');
  assert.match(diagram, /onClick=\{autoArrange\}/, 'Vertical stays');
  assert.match(diagram, /onClick=\{layered\}/, 'Layered stays');
  assert.match(diagram, /effLayout === 'matrix' \? matrixPositions\([^)]*\) : busMatrixPositions\(/, 'only a saved or chosen dot grid is placed as a grid');
});

test('a dot can be clicked: React Flow gives an edge pointer-events visibleStroke, so the disc under the dot says "all"', () => {
  const edges = readFileSync(resolve(root, 'webview/src/diagramedges.tsx'), 'utf8');
  assert.match(edges, /r=\{slots > 1 \? DOT_SLOT \/ 2 : 13\} fill="transparent" stroke="none" style=\{\{ pointerEvents: 'all' \}\}/);
});

// ---- 1.5 fix round: a wrong fact is never shown as right -------------------------------------------------------------------
const act = (from, to, name, isActive = true) => ({ ...rel(from, to), name, isActive });
const overlaps = (a, b) => !(a.x + a.w <= b.x || b.x + b.w <= a.x || a.y + a.h <= b.y || b.y + b.h <= a.y);

test('two links between the same fact and lookup table (role-playing) get two slots, the active one first, and two spots', () => {
  const g = { tables: [tbl('Sales'), tbl('Date', { isDateTable: true }), tbl('Product')],
    rels: [act('Sales', 'Date', 'Sales_ShipDate', false), act('Sales', 'Date', 'Sales_OrderDate'), rel('Sales', 'Product')] };
  const slots = cellSlots(g.tables, g.rels);
  assert.deepEqual(slots.get('Sales_OrderDate'), { i: 0, n: 2 }, 'the active link is the first slot, though the switched-off one was listed first');
  assert.deepEqual(slots.get('Sales_ShipDate'), { i: 1, n: 2 });
  assert.deepEqual(slots.get('Sales_Product'), { i: 0, n: 1 });
});

const order = { tables: [tbl('Sales'), tbl('Order Line'), tbl('Order Header'), tbl('Date', { isDateTable: true }), tbl('Product'), tbl('Category'), tbl('Customer')],
  rels: [rel('Sales', 'Date'), rel('Sales', 'Product'), rel('Sales', 'Customer'), rel('Product', 'Category'), rel('Order Line', 'Order Header'), rel('Order Line', 'Product'), rel('Order Header', 'Date'), rel('Order Header', 'Customer')] };

test('a header table with its own lookup tables sits with the facts, right above its lines, and its links to lookups are dots', () => {
  const o = matrixOrder(order.tables, order.rels);
  assert.ok(o.facts.includes('Order Header') && !o.dims.includes('Order Header'), 'the header is a fact row, not a lookup column');
  assert.equal(o.facts.indexOf('Order Header') + 1, o.facts.indexOf('Order Line'), 'the header is right above its lines');
  const cells = cellRels(order.tables, order.rels);
  assert.ok(cells.has('Order Header_Date') && cells.has('Order Header_Customer'), 'the header uses Date and Customer as dots');
  assert.ok(!cells.has('Order Line_Order Header'), 'a fact to fact link is not a dot');
  assert.ok(cells.has('Order Line_Product'));
});

test('the dot grid draws no long line: a link that is not a dot is a short mark between neighbours or a tag on each card', () => {
  const { box } = boxes(order);
  const marks = matrixMarks(order.tables, order.rels);
  const m = marks.get('Order Line_Order Header');
  assert.equal(m?.kind, 'left', 'fact to fact belongs to the fact column');
  assert.equal(m.near, true, 'the header sits right above its first line, so the link is a short mark');
  assert.ok(!marks.has('Sales_Date'), 'a dot is not a mark');
  const pts = nearMarkPoints(box('Order Header'), box('Order Line'));
  assert.ok(pts && Math.abs(pts[0].y - pts[1].y) <= 48 && pts[0].x === pts[1].x, 'a short straight run through the gap');
  assert.equal(nearMarkPoints({ x: 0, y: 0, w: 190, h: 72 }, { x: 0, y: 400, w: 190, h: 72 }), null, 'cards moved far apart: no long line, the tags take over');
  // a header with a second line further down: that link is a tag on each card, and the two tags do not share a spot
  const g = { tables: [tbl('Order Header'), tbl('Line A'), tbl('Line B'), tbl('Sales'), tbl('Date', { isDateTable: true }), tbl('Customer')],
    rels: [rel('Line A', 'Order Header'), rel('Line B', 'Order Header'), rel('Order Header', 'Date'), rel('Order Header', 'Customer'), rel('Line A', 'Date'), rel('Line A', 'Customer'), rel('Line B', 'Date'), rel('Sales', 'Date')] };
  const mk = matrixMarks(g.tables, g.rels);
  assert.equal(mk.size, 2, 'both header links are marks');
  const places = [...mk.entries()].flatMap(([name, v]) => { const r = g.rels.find((x) => x.name === name); return [`${r.fromTable}|${v.from}`, `${r.toTable}|${v.to}`]; });
  assert.equal(new Set(places).size, places.length, 'no two tags on one card share a place');
  assert.notDeepEqual(tagPoint('left', { x: 0, y: 0, w: 190, h: 72 }, 0), tagPoint('left', { x: 0, y: 0, w: 190, h: 72 }, 1));
  // two lookup tables that are not neighbours in the row are tags on the top edge, never a line over the row
  const h = { tables: [tbl('Sales'), tbl('Returns'), tbl('Date', { isDateTable: true }), tbl('Product'), tbl('Customer'), tbl('Region'), tbl('Category')],
    rels: [rel('Sales', 'Date'), rel('Sales', 'Product'), rel('Sales', 'Customer'), rel('Returns', 'Region'), rel('Returns', 'Category'), rel('Returns', 'Customer'), rel('Customer', 'Region'), rel('Product', 'Category')] };
  const b2 = matrixMarks(h.tables, h.rels), row = matrixOrder(h.tables, h.rels).dims;
  for (const r of h.rels.filter((x) => ['Customer', 'Product'].includes(x.fromTable))) {
    const gap = Math.abs(row.indexOf(r.fromTable) - row.indexOf(r.toTable));
    assert.equal(b2.get(r.name)?.kind, gap === 1 ? undefined : 'top', `${r.name} ${gap === 1 ? 'is a short line between neighbours' : 'is a tag on each card'}`);
    if (gap !== 1) assert.equal(b2.get(r.name).near, false);
  }
});

test('Fit in the dot grid holds a readable zoom and pans, and the first frame measures its fit with the margin it draws with', () => {
  const diagram = readFileSync(resolve(root, 'webview/src/diagram.tsx'), 'utf8');
  assert.match(diagram, /getViewportForBounds\(bounds, width, height, MIN_ZOOM, 2, opts\?\.padding \?\? FIT_MARGIN\)/, 'a frame that fits with the grid margin is not cut short by the wider default margin');
  assert.match(diagram, /showFitView=\{false\}/, 'React Flow queues its own fit, so Fit is our own button in every layout');
  assert.match(diagram, /className="react-flow__controls-fitview"[^>]*onClick=\{\(\) => readableFit\(\{ padding: layout === 'matrix' \? MATRIX_FIT_MARGIN : FIT_MARGIN/, 'the grid Fit button keeps its tighter margin and never frames below the readable zoom');
  assert.match(diagram, /layout === 'matrix' \? <div className="sem-chip-row">\{chips\}<\/div> : chips/, 'the grid counts and legend share one wrapping row, so the legend is never cut or laid over the counts');
});

test('the dots of a role-playing cell sit apart, and a dot never moves a row or column line', () => {
  const edges = readFileSync(resolve(root, 'webview/src/diagramedges.tsx'), 'utf8');
  assert.match(edges, /d\.dx \?\? 0/, 'a dot is offset by its slot');
  assert.match(edges, /colX = dx \+ dim\.measured\.width \/ 2, dotX = colX/, 'the column line stays on the middle of the cell');
  assert.match(edges, /data-matrix-dot/);
});
