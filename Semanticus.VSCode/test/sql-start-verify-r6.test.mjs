// Verifier round 6 of m11/sql-start (at 2f75a1d2): the ask bar's half of the blind held-out set Sol wrote for this
// round, run once (/tmp/semanticus-joint-review/lane-reports/v-sql-start-r6.md). Sol's asks are in
// test/sql-start-heldout-r6/asks.json exactly as Sol wrote them, each labelled "existing" or "make". The engine's half is
// Semanticus.Tests/SqlDraftVerifyR6Tests.cs. The coordinator's stop rule: no ask about a model that exists opens the
// Model Spec, and at least 27 of the 30 asks to make a model from a SQL source do.
//
// Why the bar matters on its own: askbox.tsx sends the words to the engine's routeAsk only when isQuestion or
// isPlaceAsk says so, and drafts a task otherwise. An ask the bar does not send can never open the Model Spec, whatever
// the engine would answer, so at least 27 of the 30 make-a-model asks must be sent. At 2f75a1d2, 6 are (A01 and A23 as
// questions; A06, A33, A41 and A60 as place asks), and the engine then opens the Model Spec for 5 of them. The first
// check is a guard and passes: no ask about a model that exists is a place ask.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');

function load(file) {
  const js = ts.transpileModule(readFileSync(file, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
    fileName: file,
  }).outputText;
  const module = { exports: {} };
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)((name) => req(name), module, module.exports);
  return module.exports;
}
const C = load(join(webview, 'src', 'composer-model.ts'));
const asks = JSON.parse(readFileSync(join(root, 'test', 'sql-start-heldout-r6', 'asks.json'), 'utf8'));
const existing = asks.filter((a) => a.label === 'existing');
const make = asks.filter((a) => a.label === 'make');
assert.equal(existing.length, 30, 'Sol wrote 30 asks about a model that exists');
assert.equal(make.length, 30, 'Sol wrote 30 asks to make a model from a SQL source');

const failures = [];

// 1. Guard: the bar never takes an ask about a model that exists for the new model wizard.
const placed = existing.filter((a) => C.isPlaceAsk(a.text));
if (placed.length) failures.push(`asks about a model that exists that the bar takes for the new model wizard: ${placed.map((a) => `${a.id} "${a.text}"`).join('; ')}`);

// 2. At least 27 of the 30 asks to make a model from a SQL source reach the engine at all. SKIPPED in the m11 join (part 2,
// 30 Sep): this check's premise is that the Ctrl J bar sends a question or a place ask to routeAsk. Kane's ruling of 27 Sep
// (Ctrl J drafts every line and routes nothing; test/ask-box.test.mjs and test/verify-askdata.test.mjs hold it) is the
// later ruling, so the bar sends none of them by design and the page's route is the Plan's field, which asks the engine.
// The engine's half, SqlDraftVerifyR6Tests, still holds 27 of 30 on route_ask and routeAsk.
console.log('SKIP - check 2, 27 of 30 make asks sent by the Ctrl J bar: Ctrl J routes nothing (Kane, 27 Sep); the engine half holds the 27 of 30');

assert.deepEqual(failures, [], `the ask bar on Sol's held-out asks:\n  ${failures.join('\n  ')}`);
console.log('sql-start-verify-r6: all checks passed');
