// Verifier proof (round 2 of m11/queries-redesign): the Queries source-text checks must hold on a Windows checkout.
//
// .gitattributes sets `* text=auto` and its own comment records that `core.autocrlf=true` is set on the machine this
// project is built on, so on Windows `webview/src/*.tsx` is checked out with CRLF. test/queries-redesign.test.mjs
// reads those files as text and matches patterns against them, so a pattern that spells out a line ending finds
// nothing and the check fails for a reason that has nothing to do with the code. CI runs on Linux, so CI stays
// green and only the person running `npm test` on Windows sees it.
//
// Round 1 proved queries-redesign.test.mjs safe this way at 55792505. The guards added in the fix round
// (verify-queries-redesign T, the M2 mutants) spell out `\n` inside two patterns and broke it:
//   /onRows: \(rs, dax) => \{([^\n]*)\},\n/
//   /onDax: \(dax) => \{([\s\S]*?)\n    \},\n/
// Left failing on purpose. It passes once those patterns tolerate a carriage return.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cpSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const CHECK = 'queries-redesign.test.mjs';
const SOURCES = ['daxlab.tsx', 'query.tsx', 'query-notebook.tsx', 'queriesask.tsx', 'querywells.tsx'];

const work = mkdtempSync(join(tmpdir(), 'semcrlf-'));
try {
  mkdirSync(join(work, 'test'), { recursive: true });
  mkdirSync(join(work, 'webview', 'src'), { recursive: true });
  cpSync(join(root, 'test', CHECK), join(work, 'test', CHECK));
  for (const f of SOURCES) {
    const text = readFileSync(join(root, 'webview', 'src', f), 'utf8');
    // A Windows checkout, byte for byte: every line ending is a carriage return and a line feed.
    writeFileSync(join(work, 'webview', 'src', f), text.split('\r').join('').split('\n').join('\r\n'));
  }
  const run = spawnSync(process.execPath, [join(work, 'test', CHECK)], { encoding: 'utf8' });
  assert.equal(run.status, 0,
    'the Queries source-text check fails when webview/src is checked out with Windows line endings, as it is on '
    + 'Windows (.gitattributes `* text=auto`): ' + String(run.stderr || run.stdout).split('\n').slice(0, 6).join(' | ').slice(0, 400));
} finally {
  rmSync(work, { recursive: true, force: true });
}
console.log('verify-queries-crlf: ok');
