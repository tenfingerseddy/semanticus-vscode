// Review round 6 of m11/diagram-route (29 Sep), on the round 5 verifier's findings (test/diagram-route-verify-r5.test.mjs,
// which stays as it was). Executes the SAME modules the webview ships: reflowCards and roomSteps
// (webview/src/diagramfit.mjs) and routeAll (webview/src/diagramroute.mjs), in the order the Diagram's settle pass runs
// them after the assistant saves a layout with some card kept (the helpers below are the verifier's own).
//
// 1. A kept card is a wall at 150 tables too (R5-1, R5-2): the cards that the round 5 sweeps left with another card on top
//    of them or with a line behind them at 150 tables end clean, and so do the ones that left a line along a border with
//    a kept card beside it (R5-4). A few 150-table cases keep a line along a card that is NOT the kept one (the gap
//    between two cards a third of the diagram away carries a dozen lines): that is roomSteps' two rounds, not the wall,
//    and is reported, not asserted.
// 2. Every card of the 30-table model kept in turn, in the assistant's grids (plain 10 and 0 px, star 20 px, Snap off and
//    on): the kept card never moves, nothing lands on it, no line runs behind a card or along a border.
// 3. Making room is the same every time, with a kept card too.
// 4. A dropped card goes to a spot flush with a card it does not face, corner to corner (R5-5): the nearest clear spot.
import assert from 'node:assert/strict';
import { reflowCards, roomSteps, clearDropped, roomBetween, linesBySide, CARD_GAP } from '../webview/src/diagramfit.mjs';
import { routeAll, drain } from '../webview/src/diagramroute.mjs';

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

const HUNDRED50 = big(15, 113, 22, 9);
const borderRuns = (graph, cards) => {   // lines running along the border of a card they do not join, for over 8 px
  const by = new Map(cards.map((c) => [c.id, c])), out = new Set();
  const lines = linksFor(graph, cards).map((l) => ({ id: l.id, from: l.from.id, to: l.to.id, src: endOn(by.get(l.from.id), l.from), dst: endOn(by.get(l.to.id), l.to) }));
  for (const [id, r] of routeAll(lines, cards)) {
    const own = lines.find((l) => l.id === id);
    for (let i = 1; i < (r.pts?.length ?? 0); i++) {
      const a = r.pts[i - 1], b = r.pts[i], x0 = Math.min(a.x, b.x), x1 = Math.max(a.x, b.x), y0 = Math.min(a.y, b.y), y1 = Math.max(a.y, b.y), v = x1 - x0 < 0.01;
      if (!v && y1 - y0 >= 0.01) continue;
      for (const c of cards) {
        if (c.id === own.from || c.id === own.to) continue;
        const at = v ? x0 : y0, e0 = v ? c.x : c.y, e1 = e0 + (v ? c.w : c.h), ov = Math.min(v ? y1 : x1, v ? c.y + c.h : c.x + c.w) - Math.max(v ? y0 : x0, v ? c.y : c.x);
        if ((Math.abs(at - e0) < 0.5 || Math.abs(at - e1) < 0.5) && ov > 8) out.add(`${id} along ${c.id}`);
      }
    }
  }
  return [...out];
};
const holds = (graph, pos, kid, snap = 0) => {
  const cards = settle(graph, pos, new Set([kid]), snap), k = cards.find((c) => c.id === kid);
  const faults = [];
  if (k.x !== pos[kid].x || k.y !== pos[kid].y) faults.push('the kept card moved');
  faults.push(...onKept(cards, kid), ...behindCards(graph, cards), ...borderRuns(graph, cards));
  return faults;
};
const ASP = ASPECT['2000x1200'];
ok('150 tables, the assistant\'s grids: the kept cards the round 5 sweeps left with a card on them, a line behind them or a line along a border are clean', () => {
  const cases = [['star', 20, '2000x1200', 'Dim 065'], ['star', 20, '1440x900', 'Dim 065'], ['plain', 0, '900x900', 'Dim 001'], ['plain', 0, '900x900', 'Fact 001'], ['plain', 0, '1440x900', 'Fact 001'],
    ['plain', 40, '900x900', 'Fact 008'], ['plain', 20, '900x900', 'Fact 014']];
  const bad = [];
  for (const [kind, gap, win, kid] of cases) {
    const f = holds(HUNDRED50, (kind === 'star' ? starGrid : plainGrid)(HUNDRED50, sizesOf(HUNDRED50), gap, ASPECT[win]), kid);
    if (f.length) bad.push(`${kind} ${gap} px, ${win}, keeping ${kid}: ${f.slice(0, 3).join('; ')}${f.length > 3 ? ` (${f.length} in all)` : ''}`);
  }
  assert.deepEqual(bad, [], `${bad.length} of ${cases.length} cases`);
});
ok('80 and 30 tables: the kept cards that left a line along a border (plain 40 px keeping Fact 008, plain 10 px keeping Inventory)', () => {
  const bad = [];
  for (const [graph, kind, gap, win, kid] of [[EIGHTY, 'plain', 40, '900x900', 'Fact 008'], [THIRTY, 'plain', 10, '1440x900', 'Inventory'], [THIRTY, 'plain', 10, '2000x1200', 'Inventory'], [THIRTY, 'plain', 10, '900x900', 'Inventory']]) {
    const f = holds(graph, (kind === 'star' ? starGrid : plainGrid)(graph, sizesOf(graph), gap, ASPECT[win]), kid);
    if (f.length) bad.push(`${kind} ${gap} px, ${win}, keeping ${kid}: ${f.slice(0, 3).join('; ')}`);
  }
  assert.deepEqual(bad, [], `${bad.length} cases`);
});
ok('the 30-table model, every card kept in turn in the plain grids 10 and 0 px and the star grid 20 px (Snap off and on): the kept card stays, nothing on it, no line behind a card or along a border', () => {
  const bad = [];
  for (const snap of [0, 20]) for (const [kind, gap] of [['plain', 10], ['plain', 0], ['star', 20]]) {
    const pos = (kind === 'star' ? starGrid : plainGrid)(THIRTY, sizesOf(THIRTY), gap, ASPECT['1440x900']);
    for (const kid of THIRTY.tables) { const f = holds(THIRTY, pos, kid, snap); if (f.length) bad.push(`${kind} ${gap} px, Snap ${snap ? 'on' : 'off'}, keeping ${kid}: ${f[0]}`); }
  }
  assert.deepEqual(bad, [], `${bad.length} of 180 saves`);
});
ok('making room with a card kept gives the very same positions twice', () => {
  const pos = starGrid(EIGHTY, sizesOf(EIGHTY), 40, ASP);
  const key = (cards) => cards.map((c) => `${c.id}:${c.x},${c.y}`).join('|');
  assert.equal(key(settle(EIGHTY, pos, new Set(['Dim 048']))), key(settle(EIGHTY, pos, new Set(['Dim 048']))));
});
ok('a dropped card goes to the nearest clear spot even when that is flush with a card it does not face, corner to corner', () => {
  const box = (id, x, y) => ({ id, x, y, w: 190, h: 92 });
  const cards = [box('C0', 240, 160), box('C1', 0, 200), box('C2', 360, 0), box('X', 151, 194)];
  const lines = new Map(cards.map((c) => [c.id, { top: 0, right: 0, bottom: 0, left: 0 }]));
  // Right of C1 and clear of C0 by the room between two cards (the corner where C1 and C0's row meet): the place round the corner.
  assert.deepEqual(clearDropped(cards, ['X'], CARD_GAP, 0, lines).X, { x: 190, y: 300 });
  // With no lines (the plain diagram) a spot is still kept CARD_GAP clear of a card it is beside, never flush with one.
  const any = clearDropped(cards, ['X'], CARD_GAP, 0, null).X;
  assert.ok(any.x % 1 === 0 && cards.slice(0, 3).every((o) => !(Math.min(any.x + 190, o.x + 190) - Math.max(any.x, o.x) > 0 && Math.min(any.y + 92, o.y + 92) - Math.max(any.y, o.y) > 0)), 'not on a card');
});

console.log(`diagram-route-r6: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
