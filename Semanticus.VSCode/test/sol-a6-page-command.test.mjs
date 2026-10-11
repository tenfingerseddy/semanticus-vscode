import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { makeLoader } from './helpers/webview-load.mjs';

const require = createRequire(import.meta.url);
const ts = require('typescript');
const source = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
const ast = ts.createSourceFile('extension.ts', source, ts.ScriptTarget.Latest, true);
const compile = (s) => ts.transpileModule(s, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
const declarations = (names) => ast.statements.filter((n) =>
  (ts.isFunctionDeclaration(n) && names.includes(n.name?.text)) ||
  (ts.isVariableStatement(n) && n.declarationList.declarations.some((d) => names.includes(d.name.getText(ast))))
).map((n) => n.getText(ast)).join('\n');
function exported(s, names, bindings = {}) {
  const module = { exports: {} };
  new Function('require', 'module', 'exports', ...Object.keys(bindings),
    compile(s) + '\nmodule.exports = { ' + names.join(', ') + ' };'
  )(require, module, module.exports, ...Object.values(bindings));
  return module.exports;
}
const fence = exported(readFileSync(new URL('../src/modelFence.ts', import.meta.url), 'utf8'),
  ['FencedMessageWriter', 'personAction', 'insideAnyAction', 'fenceIdentity']);

test('a Studio Undo command names the model its page showed, even when the host already shows another model', async () => {
  const A = 'file:/m/A.bim';
  const B = 'file:/m/B.bim';
  const posted = [];
  globalThis.window ??= globalThis;
  globalThis.addEventListener ??= () => undefined;
  globalThis.removeEventListener ??= () => undefined;
  globalThis.acquireVsCodeApi = () => ({ postMessage: (m) => posted.push(m), getState: () => ({}), setState() {} });
  const load = makeLoader();
  load('pagemodel').setPageModelIdentity(A);
  load('bridge').runHostCommand('semanticus.undo');
  const request = posted.find((m) => m.type === 'runCommand');
  assert.ok(request, 'the page actually posts its Undo command');

  const wire = [];
  const writer = new fence.FencedMessageWriter({ write: async (m) => { wire.push(m); } });
  const conn = { sendRequest: async (method) => {
    if (method === 'sessionInfo') return { modelIdentity: B };
    await writer.write({ jsonrpc: '2.0', id: wire.length + 1, method });
  } };
  const handlers = new Map();
  const vscode = { window: { showWarningMessage() {} }, commands: {
    registerCommand: (id, fn) => { handlers.set(id, fn); return { dispose() {} }; },
    executeCommand: (id, ...args) => handlers.get(id)?.(...args),
  } };
  const names = ['runPersonAction', 'registerPersonCommand', 'pageScoped', 'studioRelayHandler',
    'runWebviewHostCommand', 'undoRedoCmd', 'WEBVIEW_HOST_COMMANDS', 'WEBVIEW_CREATE_COMMANDS'];
  const host = exported(declarations(names), names, { ...fence, vscode, conn, hostModelIdentity: B,
    tree: { refresh() {} }, currentSessionId: 'session-B' });
  host.registerPersonCommand('semanticus.undo', () => host.undoRedoCmd('undo'));
  const panel = { webview: { postMessage() {} }, dispose() {} };
  await host.pageScoped(host.studioRelayHandler(panel, false))(request);
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(wire.length, 1, 'Undo sends exactly one engine request');
  assert.equal(wire[0].modelIdentity, A, 'B must refuse an Undo pressed on the still-visible A page');
});
