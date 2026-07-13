import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isEditableElementTree, parseElementTree, updateElementSelection } from '../webview/src/dataagent-schema.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'webview/src/dataagent.tsx'), 'utf8');
const harness = readFileSync(resolve(root, 'tools/uishot/harness.html'), 'utf8');
const shot = readFileSync(resolve(root, 'tools/uishot/shot.mjs'), 'utf8');

const original = JSON.stringify({
  type: 'semantic_model',
  serviceMetadata: { futureFlag: true, nested: { value: 7 } },
  elements: [{ display_name: 'Sales', is_selected: true, children: [
    { display_name: 'Amount', is_selected: true },
    { display_name: 'Key', is_selected: false },
  ] }],
});

assert.equal(isEditableElementTree(original), true);
assert.equal(parseElementTree(original).length, 1);

const excluded = JSON.parse(updateElementSelection(original, [0], false));
assert.equal(excluded.elements[0].is_selected, false, 'excluding a table must exclude its descendants');
assert.equal(excluded.elements[0].children[0].is_selected, false);
assert.equal(excluded.serviceMetadata.futureFlag, true, 'unknown datasource fields must survive schema edits');
assert.equal(excluded.serviceMetadata.nested.value, 7);

const childIncluded = JSON.parse(updateElementSelection(JSON.stringify(excluded), [0, 1], true));
assert.equal(childIncluded.elements[0].is_selected, true, 'including a child must include its ancestors');
assert.equal(childIncluded.elements[0].children[0].is_selected, false, 'including one child must not include its siblings');
assert.equal(childIncluded.elements[0].children[1].is_selected, true);

assert.equal(isEditableElementTree('{bad'), false);
assert.deepEqual(parseElementTree('{bad'), []);
assert.throws(() => updateElementSelection('[]', [0], true), /editable elements tree/);

assert.match(source, /datasourceJson\?: string \| null/,
  'the UI wire shape must carry the complete datasource document');
assert.match(source, /ElementTree elements=\{elements\} onSelectionChange=\{changeGeneratedSelection\}/,
  'generated model schemas must be editable before they are added');
assert.match(source, /label="Save schema…"[\s\S]*rpc<DataAgentWriteReport>\('updateDataAgent'/,
  'existing semantic-model schemas must use the shared dry-run and confirmed update route');
assert.doesNotMatch(source, /Existing sources — read-only v1/,
  'the shipped read-only schema contract must not return');
assert.match(harness, /datasourceJson: DA_DATASOURCE_JSON/,
  'the maintained Data Agent fixture must exercise complete datasource documents');
assert.match(harness, /Schema preview: ' \+ excluded \+ ' excluded elements/,
  'the maintained write preview must be derived from the edited schema payload');
assert.match(shot, /getAttribute\('aria-label'\)/,
  'interaction screenshots must be able to drive accessible schema controls');

console.log('data agent schema editing tests passed');
