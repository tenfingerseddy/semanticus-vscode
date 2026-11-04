// cx1 (Kane, 3 Nov, 1.7.3): an open took about two minutes and the Semanticus output log did not say where. The engine writes its own
// step lines to its stderr, but that reaches this log only when this window started the engine; when Claude Code's engine owns it, the
// lines go elsewhere. So the extension writes the steps the open result carries, and its own steps after the engine answered.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader } from './helpers/webview-load.mjs';

test('an open result with steps becomes one line per step and a total that names the slowest', () => {
  const M = makeLoader()('../../src/openTiming.ts');
  const lines = M.openTimingLines('openLive', {
    totalMs: 9212,
    steps: [
      { step: 'sign-in token', ms: 1161 }, { step: 'connect to the workspace', ms: 2093 },
      { step: 'read the model definition', ms: 2957 }, { step: 'save connection and history', ms: 169, alongside: true },
    ],
  }, [{ step: 'engine answered', ms: 9400 }, { step: 'properties', ms: 120 }, { step: 'status', ms: 80 }]);
  assert.deepEqual(lines, [
    '[open] engine step=sign-in token ms=1161',
    '[open] engine step=connect to the workspace ms=2093',
    '[open] engine step=read the model definition ms=2957',
    '[open] engine step=save connection and history ms=169 alongside',
    '[open] engine total ms=9212',
    '[open] window step=engine answered ms=9400',
    '[open] window step=properties ms=120',
    '[open] window step=status ms=80',
    '[open] done openLive total ms=9600 slowest: read the model definition 2957, connect to the workspace 2093, sign-in token 1161',
  ]);
});

test('an older engine with no steps still logs the window steps and the total', () => {
  const M = makeLoader()('../../src/openTiming.ts');
  const lines = M.openTimingLines('open', { modelName: 'Sales' }, [{ step: 'engine answered', ms: 2500 }, { step: 'status', ms: 40 }]);
  assert.deepEqual(lines, ['[open] window step=engine answered ms=2500', '[open] window step=status ms=40', '[open] done open total ms=2540 slowest: engine answered 2500, status 40']);
});

test('the extension writes these lines to the Semanticus output log when a page opens a model', () => {
  const source = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  // cx3: the import also brings the step type (a window step after the answer is marked as such).
  assert.ok(source.includes("import { openTimingLines, type OpenStepTime } from './openTiming'"));
  assert.ok(/for \(const line of openTimingLines\(msg\.method, result, openSteps\)\) out\.appendLine\(line\)/.test(source));
});
