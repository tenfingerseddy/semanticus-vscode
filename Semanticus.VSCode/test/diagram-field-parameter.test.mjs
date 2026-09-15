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

// ===================================================================================================
// The compact tool row (Kane, 2026-09-14: "each tool keeps one row of controls; counts and legends
// become quiet chips inside the canvas").
//
// Measured in the built app at 1366x768 before this change: the Diagram tool put 121px of its own chrome
// between the shell's segment strip and the canvas, in THREE rows — the Canvas/Relationships tabs plus a
// hint sentence (41px), the table controls (38px), and the counts plus the legend (34px). The canvas got
// 450px of a 768px window. These assertions pin the one-row shape so the rows cannot quietly come back.
// Source-pattern assertions, like the rest of this file: the React Flow canvas is not unit-mountable here,
// and the rendered proof is the screenshot pass plus tools/uishot.
// ===================================================================================================
const toolrow = read('webview/src/toolrow.tsx');
const css = read('webview/src/styles.css');

// The row region of the file: everything the single <ToolRow> renders. Bounded by the closing tag so a
// control added under the canvas cannot pass as a control on the row.
function rowRegion(src, what) {
  const start = src.indexOf('<ToolRow');
  if (start === -1) throw new Error(`${what}: no <ToolRow> in the source`);
  const end = src.indexOf('</ToolRow>', start);
  if (end === -1) throw new Error(`${what}: the <ToolRow> is never closed`);
  return src.slice(start, end);
}

// 9) ONE row, not three. Exactly one <ToolRow> in the file, and the two rows it replaced are gone: the old
//    audit strip (counts + legend) and the old Canvas/Relationships strip that carried the hint sentence.
assert.equal((diagram.match(/<ToolRow[\s>]/g) ?? []).length, 1,
  'the Diagram must render exactly one tool row');
assert.doesNotMatch(diagram, /\{\/\* model audit \*\/\}/,
  'the counts-and-legend row must be gone from the Diagram, not merely renamed');

// 10) The hint sentence leaves the row. It becomes an exported constant the shell can read for its page
//     notes, and the tooltip of the control it describes.
assert.match(diagram, /export const DIAGRAM_CANVAS_HINT\s*=/,
  'the canvas hint must be an exported named constant the shell can read');
assert.match(diagram, /export const DIAGRAM_NOTES\s*:/,
  'diagram.tsx must export DIAGRAM_NOTES for the page-notes popover the shell owns');
assert.match(diagram, /title=\{DIAGRAM_CANVAS_HINT\}/,
  'the canvas hint must become the tooltip of the control it describes');
assert.doesNotMatch(rowRegion(diagram, 'the Diagram tool row'), /expand a table to draw relationships</,
  'the hint sentence must not render as text on the tool row');

// 11) The named controls sit on that row, in the order Kane accepted.
assert.match(diagram, /<ToolRow[^>]*>\s*\{modeSwitch\}/,
  'the Canvas / Relationships switch must lead the tool row');
assert.match(diagram, /className="sem-seg"[\s\S]{0,400}Canvas[\s\S]{0,400}Relationships/,
  'the Canvas / Relationships pair must be the shared segmented control');
const inOrder = (src, labels, what) => {
  let at = -1;
  for (const label of labels) {
    const next = src.indexOf(label, at + 1);
    assert.notEqual(next, -1, `${what} must carry "${label}"`);
    assert.ok(next > at, `${what} must carry "${label}" after the control before it`);
    at = next;
  }
};
// The row itself, then the arrange cluster it holds. The cluster is a named group because below the fold
// width it collapses into one "Arrange" menu, and a group cannot fold while it is spelled out inline.
inOrder(rowRegion(diagram, 'the Diagram tool row'), ['New', 'arrangeControls', 'Snap', 'Fit', 'Show'], 'the Diagram tool row');
{
  const at = diagram.indexOf('const arrangeControls');
  assert.notEqual(at, -1, 'the arrange cluster must be a named group so it can fold');
  inOrder(diagram.slice(at, diagram.indexOf(');', at)), ['Vertical', 'Layered', 'Bus matrix', 'Expand all', 'Collapse all'], 'the Diagram arrange cluster');
}

// 12) The counts and the legend move INSIDE the canvas as quiet chips, positioned clear of the canvas's own
//     zoom controls (which React Flow pins to the bottom-left).
assert.match(diagram, /<CanvasChip[\s\S]{0,400}audit\.tables/,
  'the model counts must render in an in-canvas chip');
assert.match(diagram, /<CanvasChip[^>]*at="end"/,
  'the legend must render in an in-canvas chip at the far side of the canvas');
assert.match(css, /\.sem-canvas-chip-start\s*\{[^}]*left:/,
  'the chip positions must come from the shared classes, clear of the canvas zoom controls');
assert.match(css, /\.sem-canvas-chip-end\s*\{[^}]*right:/,
  'the far-side chip position must come from the shared classes');

// 13) A canvas too short for two chips keeps the counts and folds the legend into their tooltip, rather
//     than stacking chips over the picture.
assert.match(toolrow, /export const CANVAS_CHIP_MIN_H\s*=\s*260/,
  'the short-canvas rule must be a named constant at 260px');
assert.match(diagram, /canvasH\s*>=\s*CANVAS_CHIP_MIN_H/,
  'the legend chip must be gated on the measured canvas height');

// 14) Below the fold width the Show filters become one menu, and nothing wraps: the row itself refuses a
//     second line, so a control that does not fit must have folded before it could wrap.
assert.match(toolrow, /export const TOOL_ROW_FOLD_WIDTH\s*=\s*1000/,
  'the fold width must be a named constant at 1000px');
assert.match(diagram, /narrow\s*\?\s*<RowMenu label="Show"/,
  'below the fold width the Show filters must collapse into one Show menu');
assert.match(css, /\.sem-toolrow\s*\{[^}]*flex-wrap:\s*nowrap/,
  'the tool row must refuse to wrap onto a second line');
assert.match(css, /\.sem-toolrow\s*\{[^}]*height:\s*40px/,
  'the tool row must be the 40px row the design gives every tool');

// 15) The row is on the shared 24px control scale — the same buttons and segments as the rest of Studio,
//     not a private toolbar size (the old row used a local 10px tbBtn).
{
  const at = diagram.indexOf('const arrangeControls');
  const region = diagram.slice(at, diagram.indexOf('</ToolRow>', at));
  for (const tag of region.match(/<button[\s\S]*?>/g) ?? []) {
    const cls = /className="([^"]*)"/.exec(tag)?.[1] ?? '';
    assert.ok(/\bsem-btn-sm\b|\bsem-seg-item\b/.test(cls),
      `every control on the Diagram tool row must take the shared dense scale (saw className ${JSON.stringify(cls)})`);
  }
}

console.log('Diagram compact tool row tests passed');
