import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'webview/src/docrender.ts'), 'utf8');
const viewSource = readFileSync(resolve(root, 'webview/src/documentation.tsx'), 'utf8');
const extensionSource = readFileSync(resolve(root, 'src/extension.ts'), 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  fileName: 'docrender.ts',
}).outputText;
// docrender.ts imports the app's own layout and router (plain ES modules); hand it those very modules.
const layout = await import('../webview/src/diagramlayout.mjs');
const route = await import('../webview/src/diagramroute.mjs');
const module = { exports: {} };
const requireFromExtension = createRequire(resolve(root, 'package.json'));
const requireFor = (id) => (id === './diagramlayout.mjs' ? layout : id === './diagramroute.mjs' ? route : requireFromExtension(id));
new Function('require', 'module', 'exports', compiled)(requireFor, module, module.exports);
const { renderDoc, DEFAULT_DOC_CONFIG, DEFAULT_DOC_BRANDING } = module.exports;

const authoredProse = 'Lane extra context for the sales handover.';
const gt = (name, over = {}) => ({ ref: `table:${name}`, name, isHidden: false, isDateTable: false, isCalculated: false, isFieldParameter: false, columns: 2, measures: 0, keyColumns: [], hasDescription: true, ...over });
const dt = (name, over = {}) => ({ ref: `table:${name}`, name, description: `${name} described.`, isHidden: false, isCalculated: false, isDateTable: false, isCalculationGroup: false, calcGroupPrecedence: 0, hierarchies: [], partitions: [], calcItems: [], ...over });
const col = (table, name, over = {}) => ({ ref: `column:${table}/${name}`, name, table, dataType: 'Int64', isKey: false, isHidden: false, isCalculated: false, hasDescription: true, description: `${name} holds a value.`, ...over });
const msr = (name, folder, over = {}) => ({ ref: `measure:Key Measures/${name}`, name, table: 'Key Measures', displayFolder: folder, formatString: '#,0', isHidden: false, hasDescription: true, description: `${name} explained.`, expression: `SUM ( Sales[${name}] )`, ...over });
const dto = {
  header: {
    name: 'Parallel F', description: 'Generated model description.', compatibilityLevel: 1604,
    tableCount: 4, measureCount: 4, columnCount: 3, relationshipCount: 1, liveConnected: false, culture: 'en-US', defaultMode: 'Import',
  },
  graph: {
    tables: [gt('Sales', { isHidden: true }), gt('Date', { isDateTable: true }), gt('Key Measures', { isCalculated: true, measures: 4 }), gt('Evil </style><script>alert(1)</script>')],
    relationships: [{ name: 'r1', fromTable: 'Sales', fromColumn: 'Order Date', toTable: 'Date', toColumn: 'Date', fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true }],
  },
  tables: [
    dt('Sales', { isHidden: true, partitions: [{ name: 'Sales', mode: 'Import', sourceType: 'M', source: 'let Source = Sql.Database("x","y") in Source' }] }),
    dt('Date', { isDateTable: true }), dt('Key Measures', { isCalculated: true }), dt('Evil </style><script>alert(1)</script>'),
    dt('Time Intelligence', { isCalculationGroup: true, calcItems: [{ name: 'YTD', ordinal: 0, expression: 'CALCULATE ( SELECTEDMEASURE (), DATESYTD ( Date[Date] ) )' }] }),
  ],
  measures: [msr('Sales Amount', '01 Base'), msr('Gross Margin', '01 Base'), msr('Margin Helper', '_Helpers'), msr('Net Price', '', { isHidden: true })],
  columns: [col('Sales', 'Order Date', { isHidden: true }), col('Date', 'Date', { isKey: true, dataType: 'DateTime' }), col('Key Measures', 'Placeholder', { isHidden: true })],
  roles: [{ name: 'Region reader', modelPermission: 'Read', members: [], tableFilters: [{ table: 'Sales', filterExpression: '[Region] = "West"' }] }],
  kpis: [], dataSources: [], expressions: [],
  storageAvailable: false,
  modelNarrative: {
    author: 'human',
    updatedUtc: '2026-09-10T00:00:00Z',
    sections: [{ key: 'overview', markdown: authoredProse }],
  },
  readiness: { overall: 88.4, rawOverall: 88.4, grade: 'B', gatedBy: [], safeFixCount: 0, categories: [{ category: 'CopilotLimits', score: 50, weight: 1, applicable: 4, violations: 2, hasRules: true }] },
  bpa: { ruleCount: 74, violationCount: 134, autoFixable: 88 },
  prepForAi: {
    hasLinguisticSchema: true, aiInstructions: 'Prefer Net Revenue.', aiInstructionsLength: 18,
    aiSchemaExcludedFields: 2, sourceReadable: true, qnaEnabled: true,
    verifiedAnswersPresent: false, verifiedAnswerCount: 0,
  },
  perspectives: [
    { ref: 'perspective:Sales handover', name: 'Sales handover', description: 'Handover view.', members: ['table:Sales', 'measure:Sales/Margin'] },
  ],
};

const allOn = { ...DEFAULT_DOC_CONFIG };
const rendered = renderDoc(dto, allOn, DEFAULT_DOC_BRANDING);
const section = (html, id) => { const i = html.indexOf(`<section id="${id}"`); return html.slice(i, html.indexOf('</section>', i)); };

// D-104: authored overview prose is labeled, not mixed into generated facts. The label is one quiet word of provenance.
assert.match(rendered.html, /class="nar"/, 'authored overview must sit in the narrative block, not as unlabeled prose');
assert.match(rendered.html, /Written by you/, 'a human-authored overview must say it was written by you');
assert.match(rendered.html, new RegExp(authoredProse.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
assert.match(rendered.markdown, /Written by you/, 'Markdown export must mark authored prose');
assert.match(rendered.markdown, new RegExp(authoredProse.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
assert.ok(
  rendered.html.indexOf('Written by you') < rendered.html.indexOf('Generated model description'),
  'the authored marker must appear with the prose, above the generated description',
);
const byAgent = renderDoc({ ...dto, modelNarrative: { ...dto.modelNarrative, author: 'agent' } }, allOn, DEFAULT_DOC_BRANDING);
assert.match(byAgent.html, /Written by your assistant/, 'an assistant-authored overview says so');
const noNarrative = renderDoc(dto, { ...allOn, narrative: false }, DEFAULT_DOC_BRANDING);
assert.doesNotMatch(noNarrative.html, new RegExp(authoredProse.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
assert.doesNotMatch(noNarrative.markdown, new RegExp(authoredProse.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));

// K9, Kane's cuts (8 Oct): the internal numbers are out. The grade stays as one line.
for (const gone of [/CopilotLimits/, /issue\(s\)/, /auto-fixable/, /Copilot can index values/, /Linguistic schema/, /Compatibility/i, /Culture/, /Default mode/, /id="prepforai"/]) {
  assert.doesNotMatch(rendered.html, gone, `the HTML document must not carry ${gone}`);
  assert.doesNotMatch(rendered.markdown, gone, `the Markdown document must not carry ${gone}`);
}
assert.match(rendered.html, /AI readiness <span class="grade"[^>]*>B<\/span> <span class="num">88<\/span> of 100/, 'the AI readiness grade stays as one line');
assert.match(rendered.markdown, /AI readiness \*\*B\*\* \(88 of 100\)/, 'and in Markdown');
// Q&A retires in February 2027, so nothing may frame a setting as Q&A.
assert.doesNotMatch(rendered.html, /Q&amp;A|Q&A/, 'the HTML export must not frame anything as Q&A');
assert.doesNotMatch(rendered.markdown, /Q&A/, 'the Markdown export must not frame anything as Q&A');
// Jade only: no accent colour is injected, and the picker is gone from the Docs tab.
assert.doesNotMatch(source, /accentColor/, 'the renderer has no accent colour');
assert.doesNotMatch(viewSource, /accentColor|type="color"/, 'the Docs tab has no accent colour picker');
assert.doesNotMatch(viewSource, /<option value="(light|dark)"/, 'the Docs tab has no light or dark picker');
assert.match(rendered.html, /--sem-accent:#0A7A55/, 'the document is jade');
for (const key of ['bpaScorecard', 'prepForAi', 'hiddenObjects']) assert.doesNotMatch(viewSource, new RegExp(`key: '${key}'`), `the Include list no longer offers ${key}`);
// A config saved by an older version still loads: unknown keys are ignored, not an error.
const oldConfig = renderDoc(dto, { ...allOn, hiddenObjects: false, bpaScorecard: true, prepForAi: true }, { ...DEFAULT_DOC_BRANDING, accentColor: '#ff0000', theme: 'dark' });
assert.doesNotMatch(oldConfig.html, /#ff0000/, 'an old saved accent colour is never written into the document');
assert.doesNotMatch(oldConfig.html, /Copilot can index values/, 'an old saved switch cannot bring a cut section back');

// Hidden tables, columns and measures are shown and marked "hidden".
assert.match(section(rendered.html, 'tables'), /<h3>Sales<\/h3><span class="word">hidden<\/span>/, 'a hidden table is listed and marked');
assert.match(section(rendered.html, 'measures'), /Net Price<\/span><span class="word">hidden/, 'a hidden measure is listed and marked');
assert.match(section(rendered.html, 'tables'), /Order Date<\/span>[\s\S]*?<td class="muted">date<\/td>/, 'a hidden column of a hidden table is listed');

// Measures appear once, grouped by folder, helper folders (leading underscore) last; each name once.
const measures = section(rendered.html, 'measures');
for (const name of ['Sales Amount', 'Gross Margin', 'Margin Helper', 'Net Price']) {
  assert.equal((measures.match(new RegExp(`<span class="mname">${name}</span>`, 'g')) ?? []).length, 1, `${name} appears once in Measures`);
  assert.equal((rendered.html.match(new RegExp(`<span class="mname">${name}</span>`, 'g')) ?? []).length, 1, `${name} is not repeated under its table`);
}
assert.ok(measures.indexOf('>Base <') >= 0, 'a folder is shown without its sort number');
assert.ok(measures.indexOf('>_Helpers <') > measures.indexOf('>Other <') && measures.indexOf('>Other <') > measures.indexOf('>Base <'), 'the folder starting with an underscore comes last');
const mdMeasures = rendered.markdown.slice(rendered.markdown.indexOf('## Measures'));
for (const name of ['Sales Amount', 'Gross Margin']) assert.equal((mdMeasures.split(`**${name}**`).length - 1), 1, `${name} appears once in the Markdown Measures section`);

// Formulas are folded on screen and opened for print by the exported file's own script.
assert.match(rendered.html, /<details class="f"><summary>Formula<\/summary>/, 'a formula is folded on screen');
assert.match(rendered.html, /addEventListener\('beforeprint'/, 'the exported file opens every formula before printing');
assert.doesNotMatch(renderDoc(dto, { ...allOn, daxExpressions: false }, DEFAULT_DOC_BRANDING).html, /SUM \( Sales/, 'Formulas off leaves no formula text');

// Look: the reader's light or dark setting, always light on paper, no em dash in our copy, no network.
assert.match(rendered.html, /prefers-color-scheme: dark/, 'dark follows the reader');
assert.match(rendered.html, /:root:not\(\[data-theme="light"\]\)/, 'unless the page is pinned to light');
assert.match(rendered.html, /@media print/, 'print has its own rules');
assert.match(rendered.html, /@media screen and \(prefers-color-scheme: dark\)/, 'dark is screen only, so paper is always light');
for (const [name, text] of [['HTML', rendered.html], ['Markdown', rendered.markdown]]) {
  // Model text is printed as written; the test model has no dash, so any dash here is the renderer's own copy.
  assert.doesNotMatch(text, /—/, `${name} export carries no em dash of its own`);
}
assert.doesNotMatch(rendered.html, /https?:\/\/(?!www\.w3\.org)/, 'the document needs no network: no external URL');
assert.match(rendered.html, /Made with Semanticus/, 'footer');
assert.match(rendered.html, /Model documentation/, 'the header says what the page is');

// Fonts: embedded as data URIs when given, system fonts when not, and nothing but a font data URI is accepted.
const withFonts = renderDoc(dto, allOn, DEFAULT_DOC_BRANDING, { fonts: { sans: 'data:font/woff2;base64,AAAA', mono: 'url(https://x.test/a.woff2)' } });
assert.match(withFonts.html, /@font-face\{font-family:"Instrument Sans";src:url\(data:font\/woff2;base64,AAAA\)/, 'the sans font is embedded');
assert.doesNotMatch(withFonts.html, /x\.test/, 'a value that is not a font data URI is ignored');
assert.doesNotMatch(rendered.html, /@font-face/, 'with no fonts given the CSS falls back to system fonts');
assert.match(viewSource, /fonts: DOC_FONTS/, 'the Docs tab hands the renderer the embedded fonts');
assert.match(extensionSource, /`font-src \$\{webview\.cspSource\} data:`/, 'the Studio page allows data: fonts, or the preview cannot load the embedded font');

// The preview follows the editor's theme; the exported file follows the reader.
const dark = renderDoc(dto, allOn, DEFAULT_DOC_BRANDING, { theme: 'dark' });
assert.match(dark.previewHtml, /<html lang="en" data-theme="dark">/, 'the preview is pinned to the editor theme');
assert.match(dark.html, /<html lang="en">/, 'the exported file is not pinned');
assert.match(viewSource, /vscode-light/, 'the Docs tab reads the editor theme from the body class');
assert.match(viewSource, /background: 'var\(--sem-bg\)'/, 'the preview frame takes the page ground');

// Escaping: a model name that tries to close the style or open a script stays text everywhere.
assert.doesNotMatch(rendered.html, /<script>alert/, 'a model string cannot open a script');
assert.match(rendered.html, /Evil &lt;\/style&gt;&lt;script&gt;alert\(1\)&lt;\/script&gt;/, 'it is escaped, not dropped');

// Markdown: a Mermaid erDiagram under "How the tables connect", dotted when not active.
assert.match(rendered.markdown, /```mermaid\nerDiagram\n/, 'Markdown carries a Mermaid erDiagram');
assert.match(rendered.markdown, /"Sales" \}o--\|\| "Date" : "Order Date"/, 'many to one, active, the column as the label');
const inactive = renderDoc({ ...dto, graph: { ...dto.graph, relationships: [{ ...dto.graph.relationships[0], isActive: false }] } }, allOn, DEFAULT_DOC_BRANDING);
assert.match(inactive.markdown, /"Sales" \}o\.\.\|\| "Date" : "Order Date"/, 'not active is dotted');
assert.ok(rendered.markdown.indexOf('erDiagram') > rendered.markdown.indexOf('## How the tables connect'), 'the diagram sits under its heading');

// D-109: perspectives appear in both exports when included, and the Include list offers them.
assert.match(viewSource, /key: 'perspectives',\s*label: 'Perspectives'/, 'the Include list must offer Perspectives');
assert.match(rendered.html, /Sales handover/, 'HTML export must name each perspective');
assert.match(rendered.markdown, /Sales handover/, 'Markdown export must name each perspective');
const noPersp = renderDoc(dto, { ...allOn, perspectives: false }, DEFAULT_DOC_BRANDING);
assert.doesNotMatch(noPersp.html, /Sales handover/);
assert.doesNotMatch(noPersp.markdown, /Sales handover/);

// M21: the in-app preview iframe is sandboxed without allow-scripts, so the exported file's filter script
// logged two console errors every time Model > Docs opened. The export keeps its script; the preview has none.
assert.match(rendered.html, /<script>function docFilter/, 'the EXPORTED file keeps its own search filter');
assert.equal(typeof rendered.previewHtml, 'string', 'renderDoc must also return a preview-safe document');
assert.doesNotMatch(rendered.previewHtml, /<script/i, 'the previewed document must carry no script at all');
assert.match(rendered.previewHtml, /Sales handover/, 'the preview is the same document, only without the script');
assert.match(viewSource, /srcDoc=\{rendered\?\.previewHtml/, 'the preview iframe must render the script-free document');

console.log('Docs export tests passed');
