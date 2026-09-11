import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const daxlab = read('webview/src/daxlab.tsx');
const grid = read('webview/src/grid.tsx');
const wire = read('webview/src/wire.ts');

assert.match(wire, /export function rowAnnouncement/, 'both doors share one capped-row announcement');
assert.match(wire, /shown\. More rows exist\./, 'a capped set must say more rows exist');
assert.match(wire, /export function timingAnnouncement/, 'zero-ms times must be labelled, not shown as 0');
assert.match(wire, /under 1 ms/, 'a 0 ms figure is not a measured round trip');
assert.match(wire, /export function queryIdentity/, 'the result must be able to name the query that produced it');
assert.match(wire, /query\?: string/, 'the result carries the query text');
assert.match(wire, /cancelled\?: boolean/, 'a stopped query is a typed result, not a silent hang');

assert.match(daxlab, />Stop</, 'a running query must have a Stop control');
assert.match(daxlab, /stopQuery/, 'Stop must call a stop function, not a dead Running button');
assert.match(daxlab, /cancelDax/, 'Stop must ask the engine to stop the running query');
assert.match(daxlab, /The query was stopped\./, 'stopping must say so in plain words');
assert.match(daxlab, /disabled=\{idle\}/, 'Reset must not replace the editor while a query is running');
assert.match(daxlab, /rowAnnouncement\(res\.rowCount, res\.truncated\)/, 'the result pane must announce a capped set');
assert.match(daxlab, /Result of:/, 'the result pane must name the query that produced the result');
assert.match(daxlab, /timingAnnouncement/, 'zero-ms timings must go through the honest label');
assert.match(grid, /More rows exist/, 'the grid must say when more rows exist');

console.log('DAX Lab result pane copy and controls passed');
