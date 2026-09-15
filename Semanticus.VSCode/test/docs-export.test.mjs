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
const { renderDoc, DEFAULT_DOC_CONFIG, DEFAULT_DOC_BRANDING } = module.exports;

const authoredProse = 'Lane extra context for the sales handover.';
const dto = {
  header: {
    name: 'Parallel F', description: 'Generated model description.', compatibilityLevel: 1604,
    tableCount: 1, measureCount: 0, columnCount: 0, relationshipCount: 0, liveConnected: false, culture: 'en-US', defaultMode: 'Import',
  },
  graph: { tables: [], relationships: [] },
  tables: [], measures: [], columns: [], roles: [], kpis: [], dataSources: [], expressions: [],
  storageAvailable: false,
  modelNarrative: {
    author: 'human',
    updatedUtc: '2026-09-10T00:00:00Z',
    sections: [{ key: 'overview', markdown: authoredProse }],
  },
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

// D-104: authored overview prose is labeled, not mixed into generated facts.
assert.match(rendered.html, /doc-narr-card/, 'authored overview must sit in the narrative card, not as unlabeled prose');
assert.match(rendered.html, /Authored narrative/, 'HTML export must mark authored prose so a reader can tell it from generated facts');
assert.match(rendered.html, /You/, 'human-authored overview must name who wrote it');
assert.match(rendered.html, new RegExp(authoredProse.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
assert.match(rendered.markdown, /Authored narrative/, 'Markdown export must mark authored prose');
assert.match(rendered.markdown, new RegExp(authoredProse.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
assert.ok(
  rendered.html.indexOf('Authored narrative') < rendered.html.indexOf('Generated model description'),
  'the authored marker must appear with the prose, above the generated description',
);

const noNarrative = renderDoc(dto, { ...allOn, narrative: false }, DEFAULT_DOC_BRANDING);
assert.doesNotMatch(noNarrative.html, new RegExp(authoredProse.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
assert.doesNotMatch(noNarrative.markdown, new RegExp(authoredProse.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));

// D-106: a ticked Prep-for-AI section must appear in Markdown, not only HTML.
assert.match(rendered.html, /Prep for AI/, 'HTML already includes Prep for AI when ticked');
assert.match(rendered.markdown, /## Prep for AI/, 'Markdown must include the ticked Prep for AI section');
const noPrep = renderDoc(dto, { ...allOn, prepForAi: false }, DEFAULT_DOC_BRANDING);
assert.doesNotMatch(noPrep.markdown, /## Prep for AI/);
assert.doesNotMatch(noPrep.html, /id="prepforai"/);

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

console.log('Docs export provenance, Prep for AI, and perspectives tests passed');
