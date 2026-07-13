import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const extensionRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(extensionRoot, '..');
const sourceRoots = [
  resolve(extensionRoot, 'src'),
  resolve(extensionRoot, 'webview/src'),
];
const sourceFiles = sourceRoots.flatMap(walk).concat(resolve(extensionRoot, 'media/propgrid/propgrid.js'));
const visiblePropertyNames = new Set([
  'a', 'blurb', 'body', 'description', 'detail', 'emptyText', 'label', 'lead', 'message',
  'placeholder', 'q', 'question', 'steps', 'subtitle', 'tip', 'title', 'tooltip',
]);
const visibleJsxAttributeNames = new Set(['aria-label', 'ariaLabel', 'placeholder', 'title']);
const failures = [];

for (const file of sourceFiles) {
  const source = readFileSync(file, 'utf8');
  const kind = extname(file) === '.tsx' ? ts.ScriptKind.TSX
    : extname(file) === '.js' ? ts.ScriptKind.JS
      : ts.ScriptKind.TS;
  const tree = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, kind);
  visit(tree, tree, file);
}

const manifest = JSON.parse(readFileSync(resolve(extensionRoot, 'package.json'), 'utf8'));
checkText('package.json displayName', manifest.displayName);
checkText('package.json description', manifest.description);
for (const [index, keyword] of manifest.keywords.entries()) checkText(`package.json keywords[${index}]`, keyword, true);
for (const command of manifest.contributes?.commands ?? []) checkText(`package.json command ${command.command}`, command.title);
for (const submenu of manifest.contributes?.submenus ?? []) checkText(`package.json submenu ${submenu.id}`, submenu.label);
for (const views of Object.values(manifest.contributes?.views ?? {})) {
  for (const view of views) checkText(`package.json view ${view.id}`, view.name);
}
walkManifestConfiguration(manifest.contributes?.configuration, 'package.json configuration');
for (const root of ['Semanticus.Engine/workflows', 'Semanticus.Engine/workflow-templates']) {
  for (const file of walkFiles(resolve(repoRoot, root), new Set(['.md']))) {
    const lines = readFileSync(file, 'utf8').split(/\r?\n/);
    lines.forEach((line, index) => {
      const match = /^(title|description):\s*(.*)$/.exec(line);
      if (!match) return;
      const rendered = match[2].replace(/\{\{[^}]+\}\}/g, 'value');
      checkText(`${relative(repoRoot, file).replaceAll('\\', '/')}:${index + 1}`, rendered, false, true);
    });
  }
}

const copyModule = await loadTypeScriptModule(resolve(extensionRoot, 'webview/src/copy.ts'));
assert.equal(copyModule.uiLabel('impact_assessment'), 'Impact assessment');
assert.equal(copyModule.uiLabel('ConnectedAndInitialized'), 'Connected and initialized');
assert.equal(copyModule.uiLabel('DAXEquivalence'), 'DAX equivalence');
assert.equal(copyModule.uiLabel('answer-or-decline'), 'Answer or decline');
assert.equal(copyModule.uiLabel('', 'None'), 'None');

assert.deepEqual(failures, [], `User-facing copy violations:\n${failures.join('\n')}`);
console.log(`copy rules passed across ${sourceFiles.length} UI source files, package metadata, and built-in workflow catalog copy`);

function walk(directory) {
  return walkFiles(directory, new Set(['.ts', '.tsx', '.js']));
}

function walkFiles(directory, extensions) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return walkFiles(path, extensions);
    return extensions.has(extname(entry.name)) ? [path] : [];
  });
}

function visit(node, tree, file) {
  if (isTextNode(node)) {
    const text = node.text;
    const uiContext = isUiContext(node);
    checkText(location(tree, file, node), text, false, uiContext);
  }
  ts.forEachChild(node, (child) => visit(child, tree, file));
}

function isTextNode(node) {
  return ts.isStringLiteralLike(node)
    || ts.isTemplateHead(node)
    || ts.isTemplateMiddle(node)
    || ts.isTemplateTail(node)
    || ts.isJsxText(node);
}

function isUiContext(node) {
  if (ts.isJsxText(node)) return true;
  for (let current = node.parent; current; current = current.parent) {
    if (ts.isJsxAttribute(current)) return visibleJsxAttributeNames.has(current.name.text);
    if (ts.isPropertyAssignment(current) && visiblePropertyNames.has(propertyName(current.name))) return true;
    if (ts.isCallExpression(current) && current.arguments[0] && contains(current.arguments[0], node)) {
      const name = current.expression.getText();
      return /(?:showErrorMessage|showInformationMessage|showWarningMessage|showQuickPick)$/.test(name);
    }
    if (ts.isSourceFile(current) || ts.isFunctionLike(current)) break;
  }
  return false;
}

function contains(parent, child) {
  return child.pos >= parent.pos && child.end <= parent.end;
}

function propertyName(node) {
  return ts.isIdentifier(node) || ts.isStringLiteralLike(node) ? node.text : '';
}

function checkText(where, text, marketplaceKeyword = false, uiContext = false) {
  if (text.includes('\u2014')) failures.push(`${where}: em dash`);
  if (/\uFE0F/u.test(text) || /[\u{1F300}-\u{1FAFF}]/u.test(text)) failures.push(`${where}: colorful emoji`);
  if (/\bclaude\b/i.test(text) && (marketplaceKeyword || /\s/.test(text))) failures.push(`${where}: use AI Assistant, not Claude`);
  if (/\bAI assistant\b/.test(text)) failures.push(`${where}: capitalize AI Assistant`);
  if (uiContext) {
    const raw = text.match(/\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b/g) ?? [];
    for (const token of new Set(raw)) failures.push(`${where}: raw operation or enum ${token}`);
  }
}

function location(tree, file, node) {
  const { line, character } = tree.getLineAndCharacterOfPosition(node.getStart(tree));
  return `${relative(repoRoot, file).replaceAll('\\', '/')}:${line + 1}:${character + 1}`;
}

function walkManifestConfiguration(configuration, path) {
  if (!configuration) return;
  if (Array.isArray(configuration)) {
    configuration.forEach((item, index) => walkManifestConfiguration(item, `${path}[${index}]`));
    return;
  }
  if (typeof configuration !== 'object') return;
  for (const [key, value] of Object.entries(configuration)) {
    if (['title', 'description', 'markdownDescription'].includes(key) && typeof value === 'string') {
      checkText(`${path}.${key}`, value);
    }
    if (value && typeof value === 'object') walkManifestConfiguration(value, `${path}.${key}`);
  }
}

async function loadTypeScriptModule(file) {
  const source = readFileSync(file, 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    fileName: file,
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output).toString('base64')}`);
}
