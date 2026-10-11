// Sol join 6, round 2 (item a): the Model tree's "Add row filter" input box replaced a table's existing row filter without showing it, and wrote it with
// no starting value. Now the box opens with the filter the role already has for that table, and the write names it as the filter it replaces, so the
// engine compares it inside its own write. Runs the REAL compiled roleAddRlsCmd with the editor and the engine faked.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const compiledPath = join(root, 'out', 'extension.js');
assert.ok(existsSync(compiledPath), 'out/extension.js is missing. Run npm run compile first.');
const compiled = readFileSync(compiledPath, 'utf8');
const start = compiled.indexOf('async function roleAddRlsCmd');
assert.ok(start >= 0, 'roleAddRlsCmd moved');
const end = compiled.indexOf('\nasync function roleAddMemberCmd', start);
const code = compiled.slice(start, end);

function run({ roles, typedTable = 'table:Sales', typedFilter, failWrite }) {
  const calls = [], boxes = [], errors = [];
  const answers = [typedTable, typedFilter];
  const vscode = { window: {
    showInputBox: async (o) => { boxes.push(o); const a = answers.shift(); return typeof a === 'function' ? a(o) : a; },
    showErrorMessage: (m) => { errors.push(m); },
  } };
  const conn = { sendRequest: async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'listRoles') return roles;
    if (method === 'setTablePermission' && failWrite) throw new Error(failWrite);
  } };
  const tree = { refresh() {} };
  const fn = new Function('vscode', 'conn', 'tree', code + '\nreturn roleAddRlsCmd;')(vscode, conn, tree);
  return { go: () => fn({ name: 'Readers' }), calls, boxes, errors };
}

const roles = [{ name: 'Readers', tableFilters: [{ table: 'Sales', filterExpression: '[Region] = "West"' }] }];

// 1. The box shows the existing filter, and the write names it.
{
  const r = run({ roles, typedFilter: (o) => o.value + ' && [Open] = TRUE()' });
  await r.go();
  assert.equal(r.boxes[1].value, '[Region] = "West"', 'the second box opens with the filter the role already has');
  assert.match(r.boxes[1].prompt, /current filter is shown/, 'and says so');
  const write = r.calls.find((c) => c[0] === 'setTablePermission');
  assert.deepEqual(write, ['setTablePermission', 'Readers', 'table:Sales', '[Region] = "West" && [Open] = TRUE()', 'human', '[Region] = "West"'], 'the write names the filter it replaces');
}

// 2. A table with no filter yet starts empty, and the write names the empty start (a filter added since is refused by the engine).
{
  const r = run({ roles: [{ name: 'Readers', tableFilters: [] }], typedFilter: 'TRUE()' });
  await r.go();
  assert.equal(r.boxes[1].value, '', 'nothing to show');
  assert.deepEqual(r.calls.find((c) => c[0] === 'setTablePermission').slice(-1), [''], 'the write names the empty start');
}

// 3. A refusal from the engine reaches the person in the engine's words.
{
  const refusal = 'This row filter changed in the model after you opened it, so your version was not saved. Nothing was written.';
  const r = run({ roles, typedFilter: 'FALSE()', failWrite: refusal });
  await r.go();
  assert.ok(r.errors.some((e) => e.includes('changed in the model after you opened it')), `the person is told: ${r.errors}`);
}

// 4. If the current filter cannot be read, nothing is written.
{
  const r = run({ roles: null, typedFilter: 'TRUE()' });
  await r.go();
  assert.ok(!r.calls.some((c) => c[0] === 'setTablePermission'), 'no blind write');
  assert.ok(r.errors.some((e) => /Could not read the current row filter/.test(e)));
}
console.log('rls input box names the existing filter: ok');
