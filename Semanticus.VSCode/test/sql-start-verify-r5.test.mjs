// Verifier round 5 of m11/sql-start (at 33942de4): the ask bar's half of the defects in
// /tmp/semanticus-joint-review/lane-reports/v-sql-start-r5.md. The engine's half is Semanticus.Tests/SqlDraftVerifyR5Tests.cs.
// Every assertion here FAILS on that head. A fix makes them pass without editing this file.
//
// Why the bar matters on its own: askbox.tsx routes an ask as a question when isPlaceAsk says it names the new model
// wizard, and drafts a task otherwise. So an ask about the model the person already has must not be a place ask (the
// bar would answer it with a page instead of drafting the task), and an ask for a new model from SQL must be one.
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

const failures = [];
const expect = (ask, want, why) => { if (C.isPlaceAsk(ask) !== want) failures.push(`"${ask}": isPlaceAsk is ${!want}, want ${want} (${why})`); };

// 1. Asks about the new model the person already has. "new" governs nothing ("refresh the new model"), yet it is one of
//    the words that makes "model from ... sql" a place ask, so the bar answers these with the Model Spec instead of
//    drafting the task. (Ruling 2 lists "new" only between the making verb and "model".)
for (const ask of [
  'refresh the new model from the sql endpoint',
  'publish my new model from the sql warehouse',
  'create a measure in the new model from the sql warehouse',
  'document the new model from the sql database',
  'add descriptions to the new model from the sql source',
  'check the relationships in my new model from the sql warehouse',
  'compare the new model from sql with the published one',
  'make sure the new model from SQL has a date table',
]) expect(ask, false, 'an ask about a model that exists drafts a task');

// 2. The ruled pattern's own limit: a making verb that changes the model rather than makes one.
for (const ask of [
  'make the model from the sql database refresh faster',
  'make my model from sql server smaller',
  'start the model from the sql endpoint refreshing every hour',
  'make the new model from the fabric sql endpoint use direct lake',
]) expect(ask, false, 'a making verb that changes an existing model is not a new model');

// 3. One rule on both doors. The engine's \w is Unicode, the bar's is ASCII, so a name with an accent between "from"
//    and "sql" routes to the Model Spec on the engine (measured: route_ask spec, confidence 1) and drafts a task here.
for (const ask of [
  'build a model from our Café SQL database',
  'create a model from the München sql warehouse',
]) expect(ask, true, 'the engine routes it to the Model Spec, so the bar must too');

// 4. Plain asks for a new model from SQL that found the Model Spec at round 4 and find nothing at 33942de4.
for (const ask of [
  'create a data model from sql server',
  'build a tabular model from our sql warehouse',
  'build my first model from a sql database',
]) expect(ask, true, 'an ask for a new model from SQL names the new model wizard');

assert.deepEqual(failures, [], `the ask bar's isPlaceAsk:\n  ${failures.join('\n  ')}`);
console.log('sql-start-verify-r5: all checks passed');
