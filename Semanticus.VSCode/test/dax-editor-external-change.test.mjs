// The DAX editor shows the assistant's newer change (the stale-copy class of UAT 2 Oct: H4).
//
// WHAT WAS WRONG. After the person saved a measure in the DAX editor, the editor's file system kept the bytes it had just
// written (they exist so VS Code's re-read right after THAT save matches the buffer and does not ask again). Nothing ever
// dropped them when something else changed the measure. So when the assistant changed it over MCP, VS Code re-read the file
// and was handed the person's OLD bytes: the editor never showed the assistant's change, and the next Save wrote the old
// text back over it.
//
// Suspected rather than seen in the UAT (nobody reproduced it); this test proves the cause on the real DaxFileSystem
// class: a read after an assistant change must come from the engine, and a read after the person's OWN save must still
// match the buffer.
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

const fired = [];
const vscode = {
  EventEmitter: class { constructor() { this.event = () => undefined; } fire(e) { fired.push(e); } },
  FileChangeType: { Changed: 2 }, FileType: { File: 1 },
  FileSystemError: class extends Error { static Unavailable(m) { return new this(m); } },
  Uri: { parse: (s) => ({ toString: () => s, scheme: 'semanticus' }) },
  workspace: { textDocuments: [] },
};
const uri = { toString: () => 'semanticus:/measure/Sales/Total.dax', scheme: 'semanticus' };
let engineDax = 'SUM ( Sales[Amount] )';
const conn = { sendRequest: async (method) => { assert.equal(method, 'getDax'); return engineDax; } };
const make = new Function('vscode', 'uriForRef', 'refFromUri', 'daxHeaderDocs', 'DAX_SCHEME', 'displayDax', 'isDaxHeaderDoc', 'buildDaxHeader', `
  ${slice(compiled, 'class DaxFileSystem')}
  return DaxFileSystem;
`);
const DaxFileSystem = make(vscode, () => uri, () => 'measure:Sales/Total', new Set(), 'semanticus', (s) => s, () => false, () => '');
const fsys = new DaxFileSystem(() => conn);
fsys.attach(conn);
const read = async () => Buffer.from(await fsys.readFile(uri)).toString('utf8');

// The person saves their own text in the editor: the next read must match their buffer exactly.
fsys.rememberWritten(uri.toString(), Buffer.from('SUM ( Sales[Net] )', 'utf8'), { session: 'A', revision: 5 });
engineDax = 'SUM ( Sales[Net] )';
assert.equal(await read(), 'SUM ( Sales[Net] )', 'right after the person\'s own save the editor re-reads what it wrote');

// The echo of the person's own write arrives as a "human" change, carrying the commit's session and the revision the save returned: it must not drop those bytes.
fsys.signalChanged('measure:Sales/Total', false, { sessionId: 'A', revision: 5 });
assert.equal(await read(), 'SUM ( Sales[Net] )', 'the echo of the person\'s own save changes nothing');

// The assistant changes the measure over MCP.
engineDax = 'CALCULATE ( SUM ( Sales[Net] ), Sales[Region] = "North" )';
fsys.signalChanged('measure:Sales/Total', true);
assert.equal(await read(), engineDax, 'the open editor now reads the assistant\'s change, not the person\'s old bytes');
assert.ok(fired.length >= 2, 'VS Code was told the file changed');

// A walk through the history (Undo/Redo) is also not the editor's own write.
fsys.rememberWritten(uri.toString(), Buffer.from('OLD', 'utf8'), { session: 'A', revision: 5 });
engineDax = 'AFTER UNDO';
fsys.signalChanged('measure:Sales/Total', true);
assert.equal(await read(), 'AFTER UNDO');

// A human commit that is not this editor's own (a script apply, another editor) is a later revision, so it drops the kept bytes too (Sol join 6, C2).
fsys.rememberWritten(uri.toString(), Buffer.from('MINE', 'utf8'), { session: 'A', revision: 5 });
engineDax = 'WRITTEN BY A SCRIPT APPLY';
fsys.signalChanged('measure:Sales/Total', false, { sessionId: 'A', revision: 6 });
assert.equal(await read(), 'WRITTEN BY A SCRIPT APPLY', 'a later human commit is shown, not the kept bytes');

// A change that carries no revision cannot be shown to be the editor's own echo, so it drops the bytes.
fsys.rememberWritten(uri.toString(), Buffer.from('MINE', 'utf8'), { session: 'A', revision: 9 });
engineDax = 'NO REVISION';
fsys.signalChanged('measure:Sales/Total', false);
assert.equal(await read(), 'NO REVISION');

// The call site passes the commit's session and revision along with the foreign flag; the editor decides what is its own echo.
const source = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
assert.match(source, /daxFs\.signalChanged\(d\.ref, n\.origin !== 'human' \|\| !!n\.replay, \{ sessionId: n\.sessionId, revision: n\.revision \}\)/,
  'a change that is not the person\'s own write (the assistant, a history walk, another human write) drops the kept bytes');
console.log('dax editor external change: ok');
