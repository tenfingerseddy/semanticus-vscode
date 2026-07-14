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
const module = { exports: {} };
const requireFromExtension = createRequire(resolve(root, 'package.json'));
new Function('require', 'module', 'exports', compiled)(requireFromExtension, module, module.exports);
const { graphToSvg, renderDoc, DEFAULT_DOC_CONFIG, DEFAULT_DOC_BRANDING } = module.exports;

const table = (name) => ({ ref: `table:${name}`, name, isHidden: false, isDateTable: false, isCalculated: false, columns: 4, measures: 1, keyColumns: [`${name}Key`], hasDescription: false });
const relationship = (fromTable, toTable) => ({ name: `${fromTable}_${toTable}`, fromTable, fromColumn: `${toTable}Key`, toTable, toColumn: `${toTable}Key`, fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
const graph = {
  tables: [table('Fact'), table('Date'), table('Customer'), ...Array.from({ length: 13 }, (_, i) => table(`Isolated ${i + 1}`))],
  relationships: [relationship('Fact', 'Date'), relationship('Fact', 'Customer')],
};

const svg = graphToSvg(graph);
const viewBox = svg.match(/viewBox="0 0 (\d+) (\d+)"/);
assert.ok(viewBox, 'the relationship diagram must expose finite layout dimensions');
const [, width, height] = viewBox.map(Number);
assert.ok(width >= 4 * 190, `isolated tables must tile across rows instead of one magnified column (width ${width})`);
assert.ok(height < 1400, `the stress graph must not become a multi-screen disconnected stack (height ${height})`);
assert.match(svg, /data-components="14" data-isolated="13"/, 'the renderer must retain every relationship component and isolated table');
assert.match(svg, /style="max-width:\d+px"/, 'a small diagram must not be magnified beyond its intrinsic layout width');
assert.equal((svg.match(/marker-start=/g) ?? []).length, 2, 'both relationship edges must survive component layout');

const dto = {
  header: { name: 'Stress model', compatibilityLevel: 1701, tableCount: graph.tables.length, measureCount: 0, columnCount: 0, relationshipCount: 2, liveConnected: false },
  graph, tables: [], measures: [], columns: [], roles: [], kpis: [], dataSources: [], expressions: [], storageAvailable: false,
};
const rendered = renderDoc(dto, DEFAULT_DOC_CONFIG, DEFAULT_DOC_BRANDING);
assert.match(rendered.html, /data-components="14" data-isolated="13"/, 'preview and exported HTML must use the fixed shared relationship renderer');
assert.match(viewSource, /srcDoc=\{rendered\?\.html \?\? ''\}/, 'the live preview must render the shared HTML result');
assert.match(viewSource, /exportDoc\('html', rendered\.html, docName\)/, 'HTML export must write that same shared HTML result');

console.log('Docs relationship diagram tests passed');
