// The Add Row-Level Filter prompt tells the truth about an empty filter (UAT-F-S07a-4-001, UAT-F-S07b-3-002; Kane, 2 Oct).
//
// The prompt said "empty = deny all". That is false, and following it removed the role's row restriction. In Power BI
// an empty table filter means NO filter (every row is visible); blocking every row needs the explicit DAX FALSE().
// The behaviour is right and stays: an empty answer removes the filter, with no extra question. Only the words change.
//
// The real roleAddRlsCmd from the compiled extension runs here with the editor and the engine faked.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const compiledPath = join(root, 'out', 'extension.js');
assert.ok(existsSync(compiledPath), 'out/extension.js is missing. Run npm run compile first.');
const compiled = readFileSync(compiledPath, 'utf8');

function slice(source, opener) {
  const start = source.indexOf(opener);
  assert.ok(start >= 0, `out/extension.js no longer contains "${opener}". This test slices real code; find where it went.`);
  let depth = 0, body = false;
  for (let i = start; i < source.length; i++) {
    const c = source[i];
    if (c === '/' && source[i + 1] === '/') { i = source.indexOf('\n', i); if (i < 0) break; continue; }
    if (c === '\'' || c === '"' || c === '`') {
      for (i++; i < source.length; i++) { if (source[i] === '\\') { i++; continue; } if (source[i] === c) break; }
      continue;
    }
    if (c === '{') { depth++; body = true; continue; }
    if (c === '}') { depth--; if (body && depth === 0) return source.slice(start, i + 1); }
  }
  throw new Error(`could not find the end of ${opener}`);
}

const prompts = [];
const sent = [];
const answers = ['table:Store', ''];     // the table, then an EMPTY filter
const vscode = {
  window: {
    showInputBox: async (options) => { prompts.push(options.prompt); return answers.shift(); },
    showErrorMessage: () => undefined, showWarningMessage: () => undefined, showInformationMessage: () => undefined,
  },
};
const conn = { sendRequest: async (method, ...args) => { sent.push([method, ...args]); return method === 'listRoles' ? [{ name: 'Regional Managers', tableFilters: [] }] : undefined; } };
const tree = { refresh: () => undefined };
const run = new Function('vscode', 'conn', 'tree', `${slice(compiled, 'async function roleAddRlsCmd')}\nreturn roleAddRlsCmd;`)(vscode, conn, tree);
await run({ name: 'Regional Managers' });

const prompt = prompts[1] ?? '';
assert.equal(prompts.length, 2, 'the command asks for the table, then the filter, and nothing else');
assert.doesNotMatch(prompt, /deny all/i, `the prompt must not claim an empty filter denies everything: "${prompt}"`);
assert.match(prompt, /empty/i, 'the prompt says what leaving it empty does');
assert.match(prompt, /remove/i, 'leaving it empty removes this table\'s filter');
assert.match(prompt, /FALSE\(\)/, 'the prompt names the DAX that blocks every row');
assert.match(prompt, /block every row/i, 'and says that is what FALSE() is for');
assert.doesNotMatch(prompt, /—/, 'product copy has no em dash');
// The box first reads the filter the table already has (nothing here), and the write names it as the filter it replaces (Sol join 6, round 2).
assert.deepEqual(sent, [['listRoles'], ['setTablePermission', 'Regional Managers', 'table:Store', '', 'human', '']],
  'an empty answer still removes the filter, in one call and with no extra confirmation (the behaviour is right)');

// The same false claim must not live in any other user-facing string of the extension.
const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
assert.doesNotMatch(ext, /empty\s*=\s*deny all/i, 'no string in the extension says an empty filter denies all');
console.log('rls filter prompt: ok');
