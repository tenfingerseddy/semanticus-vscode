import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const calendars = read('webview/src/advmodels.tsx');
const lineage = read('webview/src/lineage.tsx');
const pkg = JSON.parse(read('package.json'));
const extension = read('src/extension.ts');

// D-028: a VS Code webview disables window.confirm, so a delete that waits on it is always inert.
assert.doesNotMatch(calendars, /window\.confirm/,
  'calendar delete must not wait on a browser-native confirmation dialog');
assert.match(calendars, /setConfirmDelete\(true\)/,
  'Delete calendar must open an in-panel confirmation');
assert.match(calendars, /rpc\('deleteCalendar', cal\.table, cal\.name\)/,
  'the confirmed action must call deleteCalendar');
assert.match(calendars, /Keep this calendar/,
  'the confirmation must offer a cancel that keeps the calendar');

// D-022: the mapping picker must not offer a string column for the Date category, and the Add path must refuse it.
assert.match(calendars, /dataType\?: string/,
  'calendar column rows must carry the column type so the mapping picker can filter');
assert.match(calendars, /calendarMappingAllowed/,
  'mapping a column onto a calendar category must go through a type check');
assert.match(calendars, /Cannot map/,
  'a refused mapping must tell the person why, not fail silently');

// Lineage deletes had the same inert window.confirm pattern (also reported with D-028).
assert.doesNotMatch(lineage, /window\.confirm/,
  'lineage delete must not wait on a browser-native confirmation dialog');
assert.match(lineage, /setConfirmSweep\(true\)/,
  'lineage bulk remove must use in-panel confirmation');
assert.match(lineage, /setConfirmItem\(item\)/,
  'lineage single delete must use in-panel confirmation');

// D-058: the palette command must not return on a missing tree-node argument.
const timeIntel = extension.slice(extension.indexOf('async function timeIntelCmd'), extension.indexOf('async function summarizeByCmd'));
assert.match(timeIntel, /n \?\?= treeView\?\.selection\[0\]/,
  'Generate Time-Intelligence must fall back to the tree selection when opened from the palette');
assert.match(timeIntel, /Select a measure in the Model tree/,
  'with no measure selected the command must say so instead of doing nothing');
assert.match(timeIntel, /n\.kind !== 'measure'/,
  'Generate Time-Intelligence must refuse a non-measure selection instead of calling the engine with it');

const palette = pkg.contributes.menus.commandPalette ?? [];
assert.ok(palette.some((e) => e.command === 'semanticus.timeIntelligence'),
  'Generate Time-Intelligence must declare a commandPalette when-clause so it is not a silent no-op from the palette');

console.log('calendar mapping, delete, and time-intelligence UI contract tests passed');
