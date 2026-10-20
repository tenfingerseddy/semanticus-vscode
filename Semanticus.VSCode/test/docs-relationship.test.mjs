import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'webview/src/docrender.ts'), 'utf8');
const viewSource = readFileSync(resolve(root, 'webview/src/documentation.tsx'), 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  fileName: 'docrender.ts',
}).outputText;
// docrender.ts imports the app's own layout and router (plain ES modules). The test hands it those very modules, so
// it runs the code the webview ships, and it does not depend on require() of an ES module (Node 20 cannot).
const layout = await import('../webview/src/diagramlayout.mjs');
const route = await import('../webview/src/diagramroute.mjs');
const module = { exports: {} };
const requireFromExtension = createRequire(resolve(root, 'package.json'));
const requireFor = (id) => (id === './diagramlayout.mjs' ? layout : id === './diagramroute.mjs' ? route : requireFromExtension(id));
new Function('require', 'module', 'exports', compiled)(requireFor, module, module.exports);
const { diagramSvg, renderDoc, DEFAULT_DOC_CONFIG, DEFAULT_DOC_BRANDING } = module.exports;

const table = (name) => ({ ref: `table:${name}`, name, isHidden: false, isDateTable: false, isCalculated: false, columns: 4, measures: 1, keyColumns: [`${name}Key`], hasDescription: false });
const relationship = (fromTable, toTable) => ({ name: `${fromTable}_${toTable}`, fromTable, fromColumn: `${toTable}Key`, toTable, toColumn: `${toTable}Key`, fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
const graph = {
  tables: [table('Fact'), table('Date'), table('Customer'), ...Array.from({ length: 13 }, (_, i) => table(`Isolated ${i + 1}`))],
  relationships: [relationship('Fact', 'Date'), relationship('Fact', 'Customer')],
};

// The stress model: a star of three linked tables and thirteen tables with no relationship. The document draws the
// linked tables with the app's bus matrix and names the rest in ONE line under the diagram; it does not draw them as cards.
const drawn = diagramSvg(graph);
assert.equal(drawn.lines, 2, 'both relationships must be drawn');
assert.equal((drawn.svg.match(/data-card=/g) ?? []).length, 3, 'only the three linked tables are drawn as cards');
assert.equal((drawn.svg.match(/data-rel=/g) ?? []).length, 2, 'each drawn line is one group with a data-rel hook');
assert.equal(drawn.isolated.length, 13, 'the thirteen tables with no relationship are returned, not drawn');
const viewBox = drawn.svg.match(/viewBox="(-?\d+) (-?\d+) (\d+) (\d+)"/);
assert.ok(viewBox && Number(viewBox[3]) > 0 && Number(viewBox[4]) > 0, 'the diagram must expose finite layout dimensions');
assert.ok(Number(viewBox[4]) < 1400, `the stress graph must not become a multi-screen stack (height ${viewBox[4]})`);

// UAT round 2 (N27): in a narrow preview the diagram drew at full size and its first card was cut at the edge, and in a wide one it shrank until the
// names were unreadable. The picture now keeps at least 60% of its size (the names stay readable) and scrolls from its left edge; print shows it whole.
assert.match(drawn.svg, /min-width:calc\(var\(--dgw\)\*0\.77px\)/, 'the diagram keeps the same readable floor as the Diagram tab (diagramfit FIRST_FIT_MIN_ZOOM): it scrolls instead of shrinking past 77%');
assert.match(source, /@media print\{[^}]*[\s\S]*\.diagram \.dg\{min-width:0/, 'a printed page still shows the whole diagram');

const dto = {
  header: { name: 'Stress model', compatibilityLevel: 1701, tableCount: graph.tables.length, measureCount: 0, columnCount: 0, relationshipCount: 2, liveConnected: false },
  graph, tables: [], measures: [], columns: [], roles: [], kpis: [], dataSources: [], expressions: [], storageAvailable: false,
};
const rendered = renderDoc(dto, DEFAULT_DOC_CONFIG, DEFAULT_DOC_BRANDING);
const notLinked = rendered.html.match(/Not linked to other tables:[^<]*<span class="fg">([\s\S]*?)<\/span>\s*<\/p>/);
assert.ok(notLinked, 'the tables with no relationship are named in one line under the diagram');
for (let i = 1; i <= 13; i++) assert.ok(notLinked[1].includes(`Isolated ${i}`), `Isolated ${i} must be named in that line`);
assert.equal((rendered.html.match(/data-card=/g) ?? []).length, 3, 'the document draws the same three cards');
assert.match(viewSource, /srcDoc=\{rendered\?\.previewHtml \?\? ''\}/,
  'the live preview must render the shared HTML result (script-free: its iframe has no allow-scripts)');
assert.match(source, /const previewHtml = html\.split\(searchScript\(\)\)/,
  'the preview must be that SAME rendered document with the script removed, never a second render');
assert.match(viewSource, /exportDoc\('html', rendered\.html, docName\)/, 'HTML export must write that same shared HTML result');

// A star with its fact table hidden from report view (the usual Power BI practice), every relationship leaving it.
// Kane's Contoso model is exactly this; the documentation drew the three dimensions and no line at all (K9).
{
  const t = (name, isHidden = false) => ({ ref: `table:${name}`, name, isHidden, isDateTable: name === 'Date', isCalculated: false, columns: 4, measures: 0, keyColumns: [], hasDescription: true });
  const r = (toTable, fromColumn, isActive = true) => ({ name: `Sales_${fromColumn}`, fromTable: 'Sales', fromColumn, toTable, toColumn: toTable === 'Date' ? 'Date' : `${toTable}Key`, fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive });
  const graph = { tables: [t('Sales', true), t('Customer'), t('Product'), t('Date')],
    relationships: [r('Customer', 'CustomerKey'), r('Product', 'ProductKey'), r('Date', 'Order Date'), r('Date', 'Delivery Date', false)] };
  const dto = { header: { name: 'Hidden fact', compatibilityLevel: 1600, tableCount: 4, measureCount: 0, columnCount: 0, relationshipCount: 4, liveConnected: false },
    graph, measures: [], columns: [], roles: [], kpis: [], dataSources: [], expressions: [], storageAvailable: false,
    tables: [{ ref: 'table:Sales', name: 'Sales', isHidden: true, isCalculated: false, isDateTable: false, isCalculationGroup: false, calcGroupPrecedence: 0, hierarchies: [], partitions: [], calcItems: [] }] };
  const { html } = renderDoc(dto, DEFAULT_DOC_CONFIG, DEFAULT_DOC_BRANDING);   // default config: hidden objects OFF
  const diagram = html.slice(html.indexOf('id="diagram"'), html.indexOf('</section>', html.indexOf('id="diagram"')));
  // Each drawn line is one group carrying data-rel="From-To".
  const lines = (diagram.match(/data-rel=/g) ?? []).length;
  assert.equal(lines, 4, `every relationship must be drawn even when its fact table is hidden (drew ${lines} of 4)`);
  assert.match(diagram, />Sales</, 'the hidden fact table must be on the diagram');
  assert.match(diagram, /data-card="Sales"[\s\S]*?>hidden</, 'and marked hidden, as the app marks it');
  assert.match(html, /<h3>Sales<\/h3>/, 'a hidden table is also described in the Tables section');
}

console.log('Docs relationship diagram tests passed');
