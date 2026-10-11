// verify: round 5 of the m11/diagram-route verification (29 Sep), left failing on purpose for the builder.
// Executes the SAME code the webview ships: reflowCards and roomSteps (webview/src/diagramfit.mjs) and routeAll
// (webview/src/diagramroute.mjs), in the order the Diagram's settle pass runs them after the person's assistant saves
// a layout (diagram.tsx: reflowCards with the kept cards, the line ends from computeAnchors on the saved spots, then
// roomSteps with the kept cards; once the cards land the ends are sorted again and every line is routed once).
//
// Round 5 made a kept card (the assistant's keep mark, or the person's Put back) a wall: a card pushed toward one goes on
// past it. Every one of the 80 cards kept one at a time, in the assistant's grids (plain 40/20/10/0 px and star
// 80/40/20 px apart, three windows, Snap off and on: 3360 saves), still leaves some kept cards with another card on top
// of them or with lines behind them. Each case below was also seen in the live page (the uishot harness with the
// lane's bundle, the grid posted the way the engine broadcasts it).
//
// 1. A card pushed sideways into a kept card's column while it sits a few px above the kept card is never checked
//    against it on the other axis (pastKept only looks at kept cards it shares height with along x, width with along
//    y), and the tidy step then lines it up with its row, down onto the kept card: the star grid 40 px apart at
//    2000 x 1200 keeping Dim 048 ends with Dim 040 over Dim 048 by 113 x 24 px and Fact 003 -> Dim 040 behind it;
//    keeping Dim 041 leaves Dim 033 on it. With Snap on (20 px grid) the same happens to six more (Dim 038 covers the
//    whole height of a kept Dim 041 in the plain grid 10 px apart at 900 x 900).
// 2. A card nothing pushed stays where the assistant saved it, even touching a kept card (a card beside a kept card
//    that nothing pushed stays, by design), so the lines leaving the side that faces the kept card start inside it:
//    the plain grid with the cards touching (0 px), keeping Fact 001 of the 80-table model, draws Dim 007 -> Dim 054
//    behind Fact 001; in the 30-table model keeping Inventory draws its neighbour's lines behind Inventory (six in the
//    live page) and lines on top of each other.
import assert from 'node:assert/strict';
import { reflowCards, roomSteps, linesBySide, CARD_GAP } from '../webview/src/diagramfit.mjs';
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
ok("the assistant's 80-table star grid 40 px apart (2000 x 1200) keeping Dim 048: no card is left on top of the kept card, no line behind it", () => {
  const pos = starGrid(EIGHTY, sizesOf(EIGHTY), 40, ASPECT['2000x1200']);
  const cards = settle(EIGHTY, pos, new Set(['Dim 048']));
  const k = cards.find((c) => c.id === 'Dim 048');
  assert.deepEqual([k.x, k.y], [pos['Dim 048'].x, pos['Dim 048'].y], 'the kept card stays where it was saved');
  assert.deepEqual(onKept(cards, 'Dim 048'), [], 'a card on top of the kept card');
  assert.deepEqual(behindCards(EIGHTY, cards), [], 'lines behind a card');
});
ok('the 80-table grids, each of these cards kept (Snap off and on): no card is left on top of the kept card', () => {
  const cases = [['star', 40, '2000x1200', 0, 'Dim 041'], ['plain', 10, '900x900', 20, 'Dim 041'], ['star', 40, '1440x900', 20, 'Loose 001'],
    ['star', 40, '2000x1200', 20, 'Loose 001'], ['star', 40, '1440x900', 20, 'Loose 002'], ['star', 40, '1440x900', 20, 'Dim 024'], ['star', 40, '1440x900', 20, 'Loose 012']];
  const bad = [];
  for (const [kind, gap, win, snap, kid] of cases) {
    const pos = (kind === 'star' ? starGrid : plainGrid)(EIGHTY, sizesOf(EIGHTY), gap, ASPECT[win]);
    const on = onKept(settle(EIGHTY, pos, new Set([kid]), snap), kid);
    if (on.length) bad.push(`${kind} ${gap} px, ${win}, Snap ${snap ? 'on' : 'off'}, keeping ${kid}: ${on.join('; ')}`);
  }
  assert.deepEqual(bad, [], `${bad.length} of ${cases.length} kept cards end with another card on top of them`);
});
ok("the assistant's grid with the cards touching (0 px) keeping one card: no line runs behind the kept card", () => {
  const bad = [];
  for (const [graph, kid] of [[EIGHTY, 'Fact 001'], [THIRTY, 'Inventory'], [THIRTY, 'Promotion']]) {
    const pos = plainGrid(graph, sizesOf(graph), 0, ASPECT['1440x900']);
    const b = behindCards(graph, settle(graph, pos, new Set([kid])));
    if (b.length) bad.push(`keeping ${kid}: ${b.slice(0, 3).join('; ')}${b.length > 3 ? ` (${b.length} in all)` : ''}`);
  }
  assert.deepEqual(bad, [], 'lines behind a card');
});

console.log(`diagram-route-verify-r5: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
