import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { makeLoader } from './helpers/webview-load.mjs';

const require = createRequire(import.meta.url);
const ts = require('typescript');
const { ParameterStructures } = require('vscode-jsonrpc');
const source = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');

test('Properties sends one positional plan request, including a request with two fields', async () => {
  const method = source.match(/private async marksRpc\([\s\S]*?\n    }/)[0];
  const js = ts.transpileModule('export class Grid { ' + method + ' }', {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  const Grid = new Function('exports', 'withModelIdentity', 'MARKS_GRID_METHODS', 'ParameterStructures',
    js + '; return exports.Grid;')({}, (_model, run) => run(), new Set(['planMeasureRun']), ParameterStructures);
  let sent, reply;
  const grid = new Grid(); grid.view = { webview: { postMessage: m => { reply = m; } } };
  await grid.marksRpc({ sendRequest: async (method, ...args) => {
    const positional = args[0] === ParameterStructures.byPosition;
    sent = { method, params: positional ? args.slice(1) : args.length === 1 ? args[0] : args };
    return { id: 'contoso-plan' };
  } }, { id: 1, method: 'planMeasureRun', args: [{ cap: 40, includePerformance: false }] });
  assert.deepEqual(sent, { method: 'planMeasureRun', params: [{ cap: 40, includePerformance: false }] });
  assert.equal(reply.result.id, 'contoso-plan');
});

test('a measure run waits beyond 30 seconds and uses the plan estimate', async () => {
  const timers = new Map(); let next = 0, receive, posted;
  const oldWindow = globalThis.window, oldApi = globalThis.acquireVsCodeApi;
  globalThis.window = {
    addEventListener: (event, fn) => { if (event === 'message') receive = fn; },
    setTimeout: (fn, ms) => { const id = ++next; timers.set(id, { fn, ms }); return id; },
    clearTimeout: id => timers.delete(id),
  };
  globalThis.acquireVsCodeApi = () => ({ postMessage: m => { posted = m; } });
  try {
    const bridge = makeLoader({ './pagemodel': { pageModelIdentity: () => 'contoso' },
      './engineupdated.mjs': { studioErrorText: String } })('./bridge.ts');
    const plan = bridge.rpc('planMeasureRun', { cap: 40 });
    receive({ data: { type: 'rpcResult', id: posted.id, result: { id: 'long-plan', estimatedSeconds: 900 } } });
    await plan;
    const run = bridge.rpc('runMeasurePlan', 'long-plan');
    const timer = [...timers.values()].at(-1);
    assert.ok(timer.ms > 900_000, `run guard is ${timer.ms} ms for a 900 second plan`);
    receive({ data: { type: 'checkState', payload: { version: 7, states: [{ measureRef: 'measure:Contoso/Amount', version: 7 }] } } });
    assert.equal(timers.size, 1);
    assert.notEqual([...timers.values()][0], timer, 'a completed row extends the wait while the run makes progress');
    receive({ data: { type: 'rpcResult', id: posted.id, result: { planId: 'long-plan', results: [] } } });
    assert.equal((await run).planId, 'long-plan');
    assert.equal(timers.size, 0);
  } finally { globalThis.window = oldWindow; globalThis.acquireVsCodeApi = oldApi; }
});
