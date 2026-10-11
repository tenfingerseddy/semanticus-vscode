// VERIFY, m11/jev-query-tune round 2 (verifier, 28 Sep). Left failing on purpose, for the builder.
//
// Golden rule 2: a capability ships on both doors. This lane taught the engine to filter a column of days by a
// range ("last week" is Date from 2025-09-29 and Date before 2025-10-06): QueryTemplate.Filterable now takes a date
// column, PartsAsync accepts after, from, before or up to on one, and the ask_data tool tells the person's assistant
// how to send one by hand. The Studio's Ask your data builder still hides every date column from "Filter on", still
// tells the person "Dates and yes or no columns cannot be filtered yet", and opens a date chip Jev made with only
// "is" and "is not" to choose from, the two a column of days refuses. So the assistant can build or change a date
// range and the person cannot. This test fails until the Studio door can do what the agent door can, or the lane
// takes the capability out of the agent door too.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(root, '..');
const webview = join(root, 'webview');
const read = (f) => readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
const ts = createRequire(join(webview, 'package.json'))('typescript');

// The agent door: the engine and the tool text say a column of days takes a range.
const template = read(join(repoRoot, 'Semanticus.Engine', 'Query', 'QueryTemplate.cs'));
const tool = read(join(repoRoot, 'Semanticus.Engine', 'McpTools.Query.cs'));
assert.match(template, /Filterable\(QueryColumn c\) =>[^;]*c\.Kind == "date"/, 'the engine no longer filters a column of days, so this test proves nothing');
assert.match(tool, /A column of days takes after, from, before or up_to/, 'the ask_data tool no longer offers a date range, so this test proves nothing');

// The Studio door: the builder's own model code, executed.
const model = { exports: {} };
new Function('require', 'module', 'exports', ts.transpileModule(read(join(webview, 'src', 'askdata-model.ts')), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText)((n) => { throw new Error('askdata-model.ts now imports ' + n + ' at run time; load it the way ask-data-page.test.mjs does'); }, model, model.exports);
const offered = model.exports.filterableColumns([
  { table: 'Date', name: 'Date', dataType: 'DateTime' }, { table: 'Date', name: 'Year', dataType: 'Int64' },
]).map((c) => c.name);
assert.ok(offered.includes('Date'),
  'Ask your data offers no column of days under "Filter on", so a person cannot build the date range the assistant can send: ' + JSON.stringify(offered));

const page = read(join(webview, 'src', 'askdata.tsx'));
assert.doesNotMatch(page, /Dates and yes or no columns cannot be filtered yet/,
  'the builder tells the person dates cannot be filtered, which the engine now does');
