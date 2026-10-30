import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import test from 'node:test';

// fx29b F: Turn on in Properties opens Studio's permission card, and the Properties card then kept "Pro advice is off for this
// model. Turn on" until Check now: its host dropped the recheck Studio's card asks for (checked-grid.tsx open ignored `then`).
// adviceRecheck is that host's half: armed by Turn on, it asks the engine on each change and checks again once advice is on.
const require = createRequire(import.meta.url);
const { adviceRecheck } = require('../out/marksModel.js');

test('Properties checks the measure again once Turn on has given permission, and only then', async () => {
  let current = false, reads = 0, rechecks = 0;
  const waiter = adviceRecheck(async () => { reads++; return { current }; });
  await waiter.changed();
  assert.equal(reads, 0, 'nothing is asked before Turn on');
  waiter.arm(() => rechecks++);
  await waiter.changed();
  assert.equal(rechecks, 0, 'a change while advice is still off does not check again');
  current = true;
  await waiter.changed();
  assert.equal(rechecks, 1, 'the change that gave permission checks the measure again');
  await waiter.changed();
  assert.equal(rechecks, 1, 'once');
  // A read that fails leaves it armed; the next change tries again.
  const failing = adviceRecheck(async () => { throw new Error('engine restarting'); });
  let again = 0; failing.arm(() => again++);
  await failing.changed();
  assert.equal(again, 0);
});

test('the Properties host arms the recheck on Turn on and reads permission through its allow-list', () => {
  const grid = readFileSync(new URL('../webview/src/checked-grid.tsx', import.meta.url), 'utf8');
  assert.match(grid, /adviceRecheck\(/, 'the grid host keeps the recheck');
  assert.match(grid, /open: \(where, ref, then\) => \{[^}]*advice\.arm\(then\)/, 'Turn on arms it with the card\'s recheck');
  assert.match(grid, /type === 'didChange'[\s\S]{0,400}advice\.changed\(\)/, 'a change asks whether advice is on now');
  const ext = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  assert.match(ext.match(/const MARKS_GRID_METHODS = new Set\(\[([^\]]*)\]\)/)[1], /'getConsent'/, 'Properties may read permission (read only)');
});
