import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// The Diagram must give a field-parameter table its own visual identity (Kane's spec: purple + an "FP" marker),
// fed by the engine's get_model_graph GraphTable.isFieldParameter flag. This pins that data contract + rendering
// so a refactor can't silently drop the flag, collapse a field parameter back into a plain CALC table, or lose
// the marker. Pattern-asserts the shared source (the react-flow canvas itself is not unit-mountable here).
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const diagram = read('webview/src/diagram.tsx');
const harness = read('tools/uishot/harness.html');

// 1) The wire type carries the flag (must match Semanticus.Engine/Protocol.cs GraphTable.IsFieldParameter).
assert.match(diagram, /export interface GraphTable \{[^}]*\bisFieldParameter:\s*boolean\b/,
  'the Diagram GraphTable wire type must expose isFieldParameter (the engine detection flag)');

// 2) A field parameter gets the purple accent, and — because it IS a calc table — is tested BEFORE the plain-calc
//    branch, so it never falls through to the teal CALC colour.
assert.match(diagram, /const FIELD_PARAM_VIOLET\s*=/, 'the field-parameter accent colour must be a named constant');
assert.match(diagram, /t\.isFieldParameter\s*\?\s*FIELD_PARAM_VIOLET\s*:\s*t\.isCalculated/,
  'the accent must colour a field parameter violet and be evaluated before the plain-calc branch');

// 3) The FP marker renders where the type indicator lives, in place of CALC (never both), with a plain-language title.
assert.match(diagram, /t\.isFieldParameter[\s\S]{0,160}>FP</,
  'a field-parameter table must show the monochrome "FP" marker');
assert.match(diagram, /title="Field parameter"/, 'the FP marker must carry a plain-language "Field parameter" tooltip');

// 4) The uishot fixture must include a field-parameter table so the screenshot exercises the new identity.
assert.match(harness, /isFieldParameter:\s*true/, 'the diagram screenshot fixture must include a field-parameter table');

// --- Table-kind filters (the toolbar Show chips) -------------------------------------------------------------

// 5) kindOf precedence: a field parameter IS a calculated table, so the kind classifier must test isFieldParameter
//    BEFORE isCalculated — an FP matches the "Field params" filter, never "Calculated".
assert.match(diagram, /kindOf\s*=\s*\(t: GraphTable\)[^;]*t\.isFieldParameter\s*\?\s*'fp'\s*:\s*t\.isCalculated\s*\?\s*'calc'\s*:\s*'data'/,
  'kindOf must classify FP before calc (FP precedence) with data as the fallthrough');

// 6) Arrange must place the FULL diagram scope (baseMembers), never just the kind-filtered view — otherwise
//    arranging with a kind hidden replaces the whole saved position map and silently drops the hidden tables'
//    coordinates (and arranging with everything hidden would wipe the map to {}).
assert.match(diagram, /const placed = layoutFn\(baseMembers,/,
  'arrangeWith must lay out baseMembers so filtered-out tables keep (arranged) positions');

// 7) Add paths must never land a table invisibly behind a kind filter: the shared add mutators re-enable the
//    added tables' kind toggles, and the canvas-drop duplicate check runs against the SCOPE, not the filtered view.
assert.match(diagram, /revealKinds\(names\)/, 'addTables/addTablesAt must reveal the added tables\' kinds');
assert.match(diagram, /const inScope = new Set\(baseMembers\.map/,
  'canvas-drop duplicate detection must use the diagram scope (baseMembers), not the kind-filtered members');

// 8) The three filter chips render in the toolbar with the product wording ("Field params", never an MS name).
assert.match(diagram, /label="Tables"/, 'the data-tables filter chip must render');
assert.match(diagram, /label="Calculated"/, 'the calculated-tables filter chip must render');
assert.match(diagram, /label="Field params"/, 'the field-parameters filter chip must render with "Field params" wording');

console.log('Diagram field-parameter identity tests passed');
