// The exported document and the app's Diagram must draw with ONE copy of the layout code (K9). If the two forked
// again, a model would look one way on the canvas and another way in the handover file. This test holds the seam:
// diagram.tsx and docrender.ts both import diagramlayout.mjs, and its static diagram for a Contoso-shaped model has
// every line, every line routed, and no line behind a card.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { staticDiagram, busMatrixPositions, computeAnchors, sizeForAnchors } from '../webview/src/diagramlayout.mjs';
import { segmentHitsBox } from '../webview/src/diagramroute.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const diagramSource = readFileSync(resolve(root, 'webview/src/diagram.tsx'), 'utf8');
const docSource = readFileSync(resolve(root, 'webview/src/docrender.ts'), 'utf8');

// 1. One copy. The functions moved; diagram.tsx and the document import them.
for (const fn of ['classifyRoles', 'computeAnchors', 'busMatrixPositions', 'sizeForAnchors', 'baryOrder', 'roleOf', 'anchorKey']) {
  assert.doesNotMatch(diagramSource, new RegExp(`function ${fn}\\b`), `diagram.tsx must not keep its own ${fn}`);
  assert.doesNotMatch(docSource, new RegExp(`function ${fn}\\b`), `docrender.ts must not keep its own ${fn}`);
}
const importLine = diagramSource.match(/import \{([^}]*)\} from '\.\/diagramlayout\.mjs'/);
assert.ok(importLine, 'diagram.tsx imports from ./diagramlayout.mjs');
for (const fn of ['classifyRoles', 'computeAnchors', 'busMatrixPositions']) assert.match(importLine[1], new RegExp(`\\b${fn}\\b`), `diagram.tsx imports ${fn} from ./diagramlayout.mjs`);
assert.match(docSource, /import \{[^}]*staticDiagram[^}]*\} from '\.\/diagramlayout\.mjs'/, 'the document draws with staticDiagram from ./diagramlayout.mjs');
assert.doesNotMatch(docSource, /dagre|busGrid|routeAll/, 'the document has no layout or router of its own');

// 2. The Contoso shape: Sales is hidden, three dimensions, four relationships (one not active), eight tables with none.
const t = (name, over = {}) => ({ name, isHidden: false, isDateTable: false, isCalculated: false, isFieldParameter: false, columns: 8, measures: 0, keyColumns: [], ...over });
const rel = (toTable, fromColumn, isActive = true) => ({ name: `Sales_${fromColumn}`, fromTable: 'Sales', fromColumn, toTable, toColumn: `${toTable}Key`, fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive });
const graph = {
  tables: [t('Sales', { isHidden: true }), t('Customer'), t('Product'), t('Date', { isDateTable: true }), ...Array.from({ length: 8 }, (_, i) => t(`Helper ${i + 1}`))],
  relationships: [rel('Customer', 'CustomerKey'), rel('Product', 'ProductKey'), rel('Date', 'Order Date'), rel('Date', 'Delivery Date', false)],
};
const ASPECT = 1.6;
const d = staticDiagram(graph, { aspect: ASPECT });
assert.equal(d.lines.length, 4, 'every relationship is a line, though its fact table is hidden');
assert.equal(d.cards.length, 4, 'the four linked tables are placed, the hidden one included');
assert.ok(d.cards.some((c) => c.t.name === 'Sales' && c.t.isHidden), 'the hidden Sales card is placed');
assert.equal(d.isolated.length, 8, 'the eight unlinked tables are returned, not placed');
for (const l of d.lines) assert.ok(l.pts && l.pts.length >= 2, `${l.id} has a route`);
const boxes = d.cards.map((c) => ({ x: c.x, y: c.y, w: c.w, h: c.h }));
for (const l of d.lines) for (let i = 1; i < l.pts.length; i++) {
  boxes.forEach((b, k) => assert.ok(!segmentHitsBox(l.pts[i - 1], l.pts[i], b), `${l.id} must not pass behind the ${d.cards[k].t.name} card`));
}
// Placement is the app's own: the same busMatrixPositions, for the same aspect, over the linked tables.
const linked = graph.tables.filter((x) => d.cards.some((c) => c.t.name === x.name));
const sides = computeAnchors(linked, graph.relationships, 'busmatrix');
const expected = busMatrixPositions(linked, graph.relationships, (n) => sizeForAnchors(sides.get(n) ?? []), ASPECT);
for (const c of d.cards) assert.deepEqual({ x: c.x, y: c.y }, expected.get(c.t.name), `${c.t.name} sits where the Diagram puts it`);

console.log('Docs diagram parity tests passed');
