// Review round 7 of m11/diagram-route (29 Sep), the coordinator's ruling on the round 6 decision: A PUT BACK STAYS PUT.
// Put back is the person saying no. After it a fresh page on that layout moves nothing (no toast, no save, no second Put
// back), including a card the page never moved that touches a kept card. Put back therefore keeps those neighbours too
// (keepBeside, webview/src/diagramfit.mjs): the cards the settle pass would still move once the put-back cards are kept
// are kept where they are, until a fresh pass has nothing left to move. Executes the SAME modules the webview ships.
import assert from 'node:assert/strict';
import * as fit from '../webview/src/diagramfit.mjs';
import { routeAll, drain } from '../webview/src/diagramroute.mjs';
const { reflowCards, roomSteps, linesBySide, CARD_GAP } = fit;

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};

// ---- the models (the drives' generators, generic names) ----
const rel = (fromTable, toTable) => ({ name: fromTable + '_' + toTable, fromTable, toTable, fromCardinality: 'Many', toCardinality: 'One' });
const dedupe = (rels) => { const seen = new Set(); return rels.filter((r) => (r.fromTable !== r.toTable && !seen.has(r.name) ? (seen.add(r.name), true) : false)); };
function thirty() {
  const facts = ['Sales', 'Inventory', 'Returns', 'Budget'];
  const dims = ['Date', 'Product', 'Customer', 'Store', 'Promotion', 'Currency', 'Geography', 'Channel', 'Warehouse', 'Supplier', 'Employee', 'Region', 'Scenario', 'Account', 'Department', 'Segment', 'Carrier', 'Reason', 'Ship Mode', 'Brand'];
  const loose = ['Notes', 'Measures', 'Parameters', 'Readme', 'Sort Order', 'Colours'];
  const rels = [];
  facts.forEach((f, fi) => { for (let k = 0; k < 7; k++) rels.push(rel(f, dims[(fi * 4 + k * 3) % 18])); });
  rels.push(rel('Customer', 'Geography'), rel('Store', 'Region'), rel('Product', 'Brand'), rel('Warehouse', 'Region'));
  return { tables: [...facts, ...dims, ...loose], relationships: dedupe(rels) };
}
function big(nFacts, nDims, nIso, perFact) {
  const pad = (i) => String(i + 1).padStart(3, '0');
  const facts = Array.from({ length: nFacts }, (_, i) => 'Fact ' + pad(i)), dims = Array.from({ length: nDims }, (_, i) => 'Dim ' + pad(i)), iso = Array.from({ length: nIso }, (_, i) => 'Loose ' + pad(i));
  const rels = [];
  facts.forEach((f, fi) => { for (let k = 0; k < perFact; k++) rels.push(rel(f, dims[(fi * 7 + k * 5) % nDims])); });
  for (let i = 0; i + 1 < Math.min(nDims, 16); i += 2) rels.push(rel(dims[i], dims[nDims - 1 - i]));
  return { tables: [...facts, ...dims, ...iso], relationships: dedupe(rels) };
}
// computeAnchors(members, rels, 'busmatrix', centers): the many side joins on its RIGHT, the one side on its BOTTOM,
// the ends sharing a side sorted by the angle to the card at the other end, frac = (i + 1) / (n + 1).
function anchorsFor(graph, centers) {
  const ends = [];
  for (const r of graph.relationships) {
    ends.push({ table: r.fromTable, other: r.toTable, rel: r.name, kind: 'source', side: 'right' });
    ends.push({ table: r.toTable, other: r.fromTable, rel: r.name, kind: 'target', side: 'bottom' });
  }
  const key = (e) => {
    const a = centers?.get(e.table), b = centers?.get(e.other); if (!a || !b) return 0;
    const dx = b.x - a.x, dy = b.y - a.y;
    return e.side === 'bottom' ? Math.atan2(dx, dy) : Math.atan2(dy, dx);
  };
  const groups = new Map();
  for (const e of ends) { const k = e.table + '|' + e.side; if (!groups.has(k)) groups.set(k, []); groups.get(k).push(e); }
  const out = new Map();
  for (const g of groups.values()) {
    if (centers) g.sort((a, b) => key(a) - key(b));
    g.forEach((e, i) => { if (!out.has(e.table)) out.set(e.table, []); out.get(e.table).push({ rel: e.rel, kind: e.kind, side: e.side, frac: (i + 1) / (g.length + 1) }); });
  }
  return out;
}
// sizeForAnchors: 30 px per anchor along the side, at least 190 x 92 (the harness measures the same sizes).
function sizesOf(graph) {
  const an = anchorsFor(graph, null), out = new Map();
  for (const t of graph.tables) {
    const n = { top: 0, right: 0, bottom: 0, left: 0 }; for (const a of an.get(t) ?? []) n[a.side]++;
    out.set(t, { w: Math.max(190, (Math.max(n.top, n.bottom) + 1) * 30), h: Math.max(92, (Math.max(n.left, n.right) + 1) * 30) });
  }
  return out;
}
// The assistant's grids (tools/uishot/diagram-route-verify-r4-drive.mjs), in whole px.
function plainGrid(graph, sz, gap, aspect) {
  const role = (n) => { const r = graph.relationships; const from = r.some((x) => x.fromTable === n), to = r.some((x) => x.toTable === n); return from && !to ? 0 : to ? 1 : 2; };
  const names = [...graph.tables].sort((a, b) => role(a) - role(b) || a.localeCompare(b));
  const cw = Math.max(...names.map((n) => sz.get(n).w)), ch = Math.max(...names.map((n) => sz.get(n).h));
  const cols = Math.max(1, Math.round(Math.sqrt(names.length * aspect * ch / cw)));
  return Object.fromEntries(names.map((n, i) => [n, { x: Math.round((i % cols) * (cw + gap)), y: Math.round(Math.floor(i / cols) * (ch + gap)) }]));
}
function starGrid(graph, sz, gap, aspect) {
  const r = graph.relationships;
  const facts = graph.tables.filter((c) => r.some((x) => x.fromTable === c) && !r.some((x) => x.toTable === c)).sort();
  const dims = graph.tables.filter((c) => r.some((x) => x.toTable === c)).sort();
  const loose = graph.tables.filter((c) => !r.some((x) => x.toTable === c || x.fromTable === c)).sort();
  const fw = Math.max(190, ...facts.map((f) => sz.get(f).w)), cw = Math.max(190, ...dims.map((d) => sz.get(d).w)), ch = Math.max(92, ...dims.map((d) => sz.get(d).h));
  const cols = Math.max(1, Math.round(Math.sqrt(dims.length * aspect * ch / cw)));
  const at = {}; let y = 0;
  for (const f of facts) { at[f] = { x: 0, y }; y += sz.get(f).h + gap; }
  const x0 = fw + gap + 60;
  dims.forEach((d, i) => { at[d] = { x: x0 + (i % cols) * (cw + gap), y: Math.floor(i / cols) * (ch + gap) }; });
  const lx = x0 + cols * (cw + gap) + 60;
  loose.forEach((t, i) => { at[t] = { x: lx + Math.floor(i / 12) * (cw + gap), y: (i % 12) * (ch + gap) }; });
  for (const k of Object.keys(at)) at[k] = { x: Math.round(at[k].x), y: Math.round(at[k].y) };
  return at;
}
const ASPECT = { '2000x1200': 2000 / 1200, '1440x900': 1440 / 900, '900x900': 1 };

// ---- the Diagram's settle pass after the assistant's save, then what it draws ----
function linksFor(graph, boxes) {
  const centers = new Map(boxes.map((b) => [b.id, { x: b.x + b.w / 2, y: b.y + b.h / 2 }]));
  const ends = new Map();
  for (const [table, list] of anchorsFor(graph, centers)) for (const a of list) ends.set(a.rel + ':' + a.kind, { id: table, side: a.side, frac: a.frac });
  return graph.relationships.map((r) => ({ id: r.name, from: ends.get(r.name + ':source'), to: ends.get(r.name + ':target') }));
}
function settle(graph, pos, keep, grid = 0) {
  const sz = sizesOf(graph), an = anchorsFor(graph, null);
  const lines = new Map(graph.tables.map((t) => [t, linesBySide(an.get(t) ?? [])]));
  const cards = graph.tables.map((t) => ({ id: t, ...pos[t], ...sz.get(t) }));
  const grown = reflowCards(cards, Object.fromEntries(cards.map((c) => [c.id, { w: c.w, h: c.h }])), CARD_GAP, keep);
  const boxes = cards.map((c) => ({ id: c.id, ...grown[c.id], w: c.w, h: c.h }));
  const room = drain(roomSteps(boxes, linksFor(graph, boxes), lines, grid, keep));
  return boxes.map((b) => ({ ...b, ...(room[b.id] ?? {}) }));
}
const endOn = (c, e) => (e.side === 'bottom' ? { x: c.x + e.frac * c.w, y: c.y + c.h + 4, side: 'bottom' } : { x: c.x + c.w + 4, y: c.y + e.frac * c.h, side: 'right' });
function behindCards(graph, cards) {
  const by = new Map(cards.map((c) => [c.id, c]));
  const lines = linksFor(graph, cards).map((l) => ({ id: l.id, src: endOn(by.get(l.from.id), l.from), dst: endOn(by.get(l.to.id), l.to) }));
  const routes = routeAll(lines, cards), out = new Set();
  for (const [id, r] of routes) for (let i = 1; i < (r.pts?.length ?? 0); i++) {
    const a = r.pts[i - 1], b = r.pts[i];
    for (const c of cards) if (Math.min(a.x, b.x) < c.x + c.w - 0.5 && Math.max(a.x, b.x) > c.x + 0.5 && Math.min(a.y, b.y) < c.y + c.h - 0.5 && Math.max(a.y, b.y) > c.y + 0.5) out.add(`${id} behind ${c.id}`);
  }
  return [...out];
}
const onKept = (cards, kid) => {
  const k = cards.find((c) => c.id === kid);
  return cards.filter((c) => c !== k && Math.min(c.x + c.w, k.x + k.w) - Math.max(c.x, k.x) > 0.5 && Math.min(c.y + c.h, k.y + k.h) - Math.max(c.y, k.y) > 0.5)
    .map((c) => `${c.id} over ${kid} by ${Math.round(Math.min(c.x + c.w, k.x + k.w) - Math.max(c.x, k.x))} x ${Math.round(Math.min(c.y + c.h, k.y + k.h) - Math.max(c.y, k.y))} px`);
};
const EIGHTY = big(8, 60, 12, 9), THIRTY = thirty();

// ---- Put back: the cards the page moved go back and are kept; the page then keeps what it would still move ----
const movedBy = (pos, cards) => cards.filter((c) => Math.abs(c.x - pos[c.id].x) > 0.5 || Math.abs(c.y - pos[c.id].y) > 0.5).map((c) => c.id);
const movedFirst = (graph, pos, grid = 0) => movedBy(pos, settle(graph, pos, new Set(), grid));
function putBack(graph, pos, grid = 0) {
  const moved = movedFirst(graph, pos, grid);
  const keep = new Set(moved);   // what the person's Put back marks: the cards the page moved, back where they were
  const sz = sizesOf(graph), an = anchorsFor(graph, null);
  const lines = new Map(graph.tables.map((t) => [t, linesBySide(an.get(t) ?? [])]));
  const cards = graph.tables.map((t) => ({ id: t, ...pos[t], ...sz.get(t) }));
  const beside = [...drain(fit.keepBeside(cards, (boxes) => linksFor(graph, boxes), lines, grid, keep))];
  return { moved, beside, keep: new Set([...moved, ...beside]), onlyMoved: keep };
}
const GRIDS = { plain40: [plainGrid, 40], plain20: [plainGrid, 20], plain10: [plainGrid, 10], plain0: [plainGrid, 0], star80: [starGrid, 80], star40: [starGrid, 40], star20: [starGrid, 20] };
const layoutOf = (graph, name, aspect) => { const [f, gap] = GRIDS[name]; return f(graph, sizesOf(graph), gap, aspect); };

ok('the repro: 30 tables, plain 20 px, 1440x900: with only the moved cards kept a fresh page moves Budget and Product (the premise)', () => {
  const pos = layoutOf(THIRTY, 'plain20', ASPECT['1440x900']);
  const moved = movedFirst(THIRTY, pos);
  assert.ok(moved.length > 0, 'the first page moves some cards');
  assert.deepEqual(movedBy(pos, settle(THIRTY, pos, new Set(moved))).sort(), ['Budget', 'Product']);
});
ok('the repro: after a Put back that keeps the neighbours, a fresh page moves 0 of 30 cards', () => {
  const pos = layoutOf(THIRTY, 'plain20', ASPECT['1440x900']);
  const p = putBack(THIRTY, pos);
  assert.deepEqual(p.beside.sort(), ['Budget', 'Product']);
  assert.deepEqual(movedBy(pos, settle(THIRTY, pos, p.keep)), []);
});
ok('30 tables, the assistant\'s 7 grids at 3 canvas shapes, Snap off and on: after a Put back a fresh page moves 0 cards', () => {
  const bad = [];
  for (const grid of [0, 20]) for (const g of Object.keys(GRIDS)) for (const [shape, aspect] of Object.entries(ASPECT)) {
    const pos = layoutOf(THIRTY, g, aspect), p = putBack(THIRTY, pos, grid);
    const again = movedBy(pos, settle(THIRTY, pos, p.keep, grid));
    if (again.length) bad.push(`${g} ${shape} snap ${grid}: ${again.join(', ')}`);
  }
  assert.deepEqual(bad, []);
});
ok('80 tables, three grids, Snap off and on: after a Put back a fresh page moves 0 cards', () => {
  const bad = [];
  for (const grid of [0, 20]) for (const g of ['plain40', 'plain10', 'star40']) {
    const pos = layoutOf(EIGHTY, g, ASPECT['1440x900']), p = putBack(EIGHTY, pos, grid);
    const again = movedBy(pos, settle(EIGHTY, pos, p.keep, grid));
    if (again.length) bad.push(`${g} snap ${grid}: ${again.join(', ')}`);
  }
  assert.deepEqual(bad, []);
});
ok('the neighbours are never cards the page moved, and a layout with room keeps nothing extra', () => {
  const pos = layoutOf(THIRTY, 'plain40', ASPECT['1440x900']), p = putBack(THIRTY, pos);
  assert.ok(p.beside.every((id) => !p.moved.includes(id)));
  const roomy = Object.fromEntries(THIRTY.tables.map((t, i) => [t, { x: (i % 6) * 1400, y: Math.floor(i / 6) * 1200 }]));
  assert.deepEqual(putBack(THIRTY, roomy).beside, []);
});
ok('keepBeside gives the same cards twice', () => {
  const pos = layoutOf(THIRTY, 'plain20', ASPECT['1440x900']);
  assert.deepEqual(putBack(THIRTY, pos).beside, putBack(THIRTY, pos).beside);
});

console.log(`diagram-route-r7: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
