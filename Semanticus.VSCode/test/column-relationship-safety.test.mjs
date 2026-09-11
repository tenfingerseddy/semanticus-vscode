import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const ext = read('src/extension.ts');
const pkg = JSON.parse(read('package.json'));
const diagram = read('webview/src/diagram.tsx');
const propgrid = read('tools/uishot/propgrid.html');

// D-038: the New Relationship picker only offers same-type columns.
assert.match(ext, /listColumns/, 'the relationship picker must read column types');
assert.match(ext, /c\.dataType !== fromType/, 'mismatched types must be filtered out of the picker');
assert.match(ext, /No columns of the same type on other tables/, 'an empty same-type set must say why');
assert.match(diagram, /srcType !== tgtType/, 'the diagram must refuse a mismatched drag before it creates');
assert.match(diagram, /A relationship needs two columns of the same type/, 'the diagram toast must name the type rule');

// D-040: a second active relationship between the same tables is named as a table-pair, not a column-pair.
assert.match(diagram, /An active relationship already exists between those tables/, 'the diagram must name the table pair');

// D-041: default summarization options follow the column type.
assert.match(ext, /\['None', 'Sum', 'Average'/, 'number columns keep Sum in the summarization picker');
assert.match(ext, /\['None', 'Count', 'DistinctCount'\]/, 'text columns must not offer Sum');

// D-053: Sort by column is on the property grid fixture (the shared surface).
assert.match(propgrid, /SortByColumn/, 'the column grid fixture must include Sort by column');
assert.match(propgrid, /Sort by column/, 'the label a person reads must be Sort by column');

// D-045: a calculated-table column delete surfaces the engine refusal, not a fake success.
assert.match(ext, /\/formula\/i\.test\(msg\)/, 'a formula-made column delete must show the refusal, not a generic failure');

// D-156: rename warns that reports are not rewritten.
assert.match(ext, /Names in reports are not/, 'the Input Box rename must warn about reports');
assert.match(ext, /result\?\.warning/, 'a rename warning from the engine must be shown');

// D-161: inactive relationships get a distinct tree icon.
assert.match(ext, /debug-disconnect/, 'inactive relationships must not share the active relationship icon');
assert.match(ext, /n\.name\.includes\('\(inactive\)'\)/, 'the inactive tree marker is the \(inactive\) suffix');

// D-065: F2 starts the Input Box rename (the path that actually appears when the tree has focus).
const f2 = pkg.contributes.keybindings.find((k) => k.key === 'f2');
assert.equal(f2?.command, 'semanticus.renameObjectInputBox');

console.log('column and relationship safety UI contract tests passed');
