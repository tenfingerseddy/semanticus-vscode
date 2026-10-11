// Join 11 solui, U1 (Sol's A6 finding 1): a Studio Undo posted as a runCommand message named no model, so the host's newer model took its place and the Undo removed an edit from a
// model the person had not seen. The class: EVERY message a page posts to the host that can lead to a write names the model the page shows, from ONE place, and every host receiver
// that runs such a message runs it as the person's action on THAT model. These are the static facts; test/sol-a6-page-command.test.mjs is the behavioural proof for Undo.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader } from './helpers/webview-load.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'package.json'));
const ts = require('typescript');
const parse = (file) => { const text = readFileSync(file, 'utf8'); return { text, ast: ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true) }; };
const walk = (node, fn) => { fn(node); ts.forEachChild(node, (c) => walk(c, fn)); };
const files = (dir) => readdirSync(dir, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? files(join(dir, e.name)) : [join(dir, e.name)]);

test('the webview has ONE call that posts to the host, and it stamps the page model after the message (a message cannot name another)', () => {
  const sites = [];
  for (const file of files(join(root, 'webview', 'src')).filter((f) => /\.(ts|tsx)$/.test(f) && !f.endsWith('.d.ts'))) {
    const { text, ast } = parse(file);
    assert.ok(!/acquireVsCodeApi\s*\(/.test(text) || file.endsWith('bridge.ts'), `${file} must not open its own host channel; only bridge.ts does`);
    walk(ast, (n) => {
      if (ts.isCallExpression(n) && /^(vscode|api|host|acquired)\.postMessage$/.test(n.expression.getText(ast))) {
        let fn = n; while (fn && !ts.isFunctionDeclaration(fn)) fn = fn.parent;
        sites.push({ file, fn: fn?.name?.text, text: n.getText(ast) });
      }
    });
  }
  assert.equal(sites.length, 1, `exactly one vscode.postMessage in webview/src, found ${sites.map((s) => `${s.file}:${s.fn}`).join(', ')}`);
  assert.ok(sites[0].file.endsWith('bridge.ts') && sites[0].fn === 'postToHostStamped');
  assert.match(sites[0].text, /\{\s*\.\.\.message\s*,\s*modelIdentity:\s*pageModelIdentity\(\)\s*\}/, 'the spread comes first, so the stamp wins');
});

test('every message type the bridge posts reaches the host carrying the page model identity', () => {
  const posted = [];
  globalThis.window ??= globalThis;
  globalThis.addEventListener ??= () => undefined;
  globalThis.removeEventListener ??= () => undefined;
  globalThis.acquireVsCodeApi = () => ({ postMessage: (m) => posted.push(m), getState: () => ({}), setState() {} });
  const A = 'file:/m/A.bim';
  const load = makeLoader();
  load('pagemodel').setPageModelIdentity(A);
  const bridge = load('bridge');
  const { ast } = parse(join(root, 'webview', 'src', 'bridge.ts'));
  const types = new Set();
  walk(ast, (n) => {
    if (ts.isCallExpression(n) && n.expression.getText(ast) === 'postToHostStamped' && ts.isObjectLiteralExpression(n.arguments[0] ?? {})) {
      const t = n.arguments[0].properties.find((p) => p.name?.getText(ast) === 'type');
      if (t && ts.isStringLiteral(t.initializer)) types.add(t.initializer.text);
    }
  });
  assert.ok(types.size >= 20, `the bridge posts many message types (found ${types.size})`);
  // Drive the ones that post on a plain call, then check each posted message; the rest are covered by the single-call fact above.
  bridge.runHostCommand('semanticus.undo');
  bridge.focusModelTree();
  bridge.signalReady();
  bridge.openLocalModel();
  bridge.postStudioZoom(100);
  bridge.postToHost({ type: 'draftsHeld', value: {} });
  bridge.closeConnectionsPanel();
  void bridge.saveModelFromPage();
  void bridge.rpc('listTables').catch(() => undefined);
  assert.ok(posted.length >= 9, `posted ${posted.length}`);
  for (const m of posted) assert.equal(m.modelIdentity, A, `${m.type} carries the page model`);
});

test('the host runs every Studio page message, and the Properties grid Undo, as the person\'s action on the model the page named', () => {
  const { text } = parse(join(root, 'src', 'extension.ts'));
  const studio = text.match(/onDidReceiveMessage\(pageScoped\(studioRelayHandler\(/g) ?? [];
  assert.equal(studio.length, 2, 'both Studio relay registrations (Studio and the standalone Connections panel) go through pageScoped');
  assert.match(text, /type === 'runCommand'\) \{ await runPersonAction\(\[\], \(\) => runWebviewHostCommand\([^)]*\), msg\.model\)/, 'the Properties grid runCommand runs on the model the grid names');
  const grid = readFileSync(join(root, 'media', 'propgrid', 'propgrid.js'), 'utf8');
  assert.match(grid, /type: 'runCommand', command, model: heldModel/, 'the grid posts the model it shows with its Undo');
});
