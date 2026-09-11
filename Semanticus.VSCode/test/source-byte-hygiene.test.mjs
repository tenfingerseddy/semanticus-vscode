// Every tracked text file is swept for RAW CONTROL BYTES.
//
// WHY THIS EXISTS. On 2026-07-31 three raw NUL bytes sat in WorkflowParser.cs, written when a `'\0'` char
// literal went through a shell and the backslash-zero collapsed into byte 0x00. That is the FIFTH recorded
// instance of this hazard in this repository, and the fourth one struck inside its own countermeasure.
// CLAUDE.md has documented the rule for months. Prose did not prevent instance five, so this is the
// countermeasure that is not prose.
//
// WHY NOTHING CAUGHT IT, all four measured on the instance itself:
//   1. no repo-wide control-byte sweep existed; the nearest check scans for endpoint strings, not bytes.
//   2. the byte verification that IS routine here was aimed at new documents and fixtures, never at the
//      source file being edited, so the ritual covered the safe half of the work.
//   3. the compiler was happy. A raw NUL inside a char literal is a VALID char literal, so the build was
//      green, 2516 tests passed and CI agreed. The code was correct and only unreadable.
//   4. review integrity survived by luck. git decides binary-or-text from the first 8 KB and these bytes
//      sat at offset 105,883, so every diff still rendered. Nearer the top of the file, every review of it
//      from that commit onward would have been reading a diff git refused to show.
//
// WHAT IT CHECKS. Every file `git ls-files` reports, minus a declared binary extension list, must contain
// no byte below 0x20 other than tab and newline, no 0x7F, and no carriage return EXCEPT as the CR half of
// a CRLF pair: a lone CR is a control byte like any other, because it broke a rendered table twice while
// reading as legal to a CRLF-conventioned file. An extension NOT on that
// list is scanned, so the default is fail-closed: a new binary format has to be declared before it can be
// skipped, and forgetting to declare one produces a loud failure rather than a silent exemption. The same
// now holds for a file that cannot be READ: the only skips are "not in the working tree" and "a submodule
// gitlink", and every other read failure fails the sweep by name. That sentence was here before the code did
// it (F-064), which is the same defect this file was written to catch, one level up.
import { test } from 'node:test';
import assert from 'node:assert';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdtempSync, rmSync, lstatSync } from 'node:fs';
import { resolve, join, extname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(fileURLToPath(new URL('../..', import.meta.url)));

// Binary by nature. Kept short and explicit: every addition here is a file whose bytes nobody reviews,
// which is exactly the place a hidden byte would be safest, so the list should stay hard to grow.
const BINARY_EXTENSIONS = new Set(['.png', '.jpg', '.jpeg', '.gif', '.ico', '.pbix', '.vsix', '.zip', '.dll', '.exe', '.pdf', '.woff', '.woff2', '.ttf', '.snk']);

/**
 * Read one tracked path for the sweep, and CLASSIFY a failure rather than swallowing it.
 *
 * TWO legitimate skips, both of which mean there is no file here to sweep: the path is not in the working
 * tree (a tracked-but-deleted file), or it is a submodule gitlink, which git reports as a tracked path and
 * the filesystem reports as a directory. Every other failure means the sweep could not examine a file it is
 * responsible for, and a sweep that cannot examine a file must say so: a blanket `catch { continue }`
 * exempted the file AND anything hidden in it, silently, while the header of this file claimed the default
 * was fail-closed. Making the claim true was the choice; the alternative was deleting the claim.
 */
export function readTracked(abs, rel, io = { lstat: lstatSync, readFile: readFileSync }) {
    // Classified by what the path IS, not by which errno the read happened to produce. Deciding from the
    // errno alone cannot tell a submodule gitlink from a file whose permissions are wrong, and guessing
    // wrong in the permissive direction is the whole finding.
    let st;
    try {
        st = io.lstat(abs);
    } catch (err) {
        if (err.code === 'ENOENT') return { skip: 'not in the working tree' };
        throw new Error(`${rel} is tracked but could not be examined (${err.code}), so it was NOT swept.`);
    }
    if (st.isDirectory()) return { skip: 'a submodule gitlink, not a file' };
    try {
        return { buf: io.readFile(abs) };
    } catch (err) {
        throw new Error(
            `${rel} is a tracked FILE that could not be read (${err.code}), so it was NOT swept. `
            + 'A file this sweep cannot open is not a file this sweep has cleared. Fix the permissions, or '
            + 'declare its extension binary if that is what it is.');
    }
}

/** The offending bytes in a buffer: anything under 0x20 that is not tab/LF/CR, plus DEL, plus a LONE CR.
 * A carriage return is only legal as half of CRLF. A lone one is invisible in every review surface while
 * corrupting the render: F-085 documented one breaking a Markdown table, and the fix for THAT row then
 * fused two rows by dropping a newline, caught only by the register's cell count. This rule was proposed
 * by the review on #309 after F-085's first citation named a test that could not catch the defect. */
function controlBytes(buf) {
    const hits = [];
    for (let i = 0; i < buf.length; i++) {
        const b = buf[i];
        if (b === 0x09 || b === 0x0a) continue;
        if (b === 0x0d) {
            if (buf[i + 1] !== 0x0a) hits.push({ offset: i, byte: b, lone: true });
            continue;
        }
        if (b < 0x20 || b === 0x7f) hits.push({ offset: i, byte: b });
    }
    return hits;
}

/** The 1-based line an offset falls on, so a failure names somewhere a human can look. */
function lineOf(buf, offset) {
    let line = 1;
    for (let i = 0; i < offset && i < buf.length; i++) if (buf[i] === 0x0a) line++;
    return line;
}

test('the sweep actually fires: a planted control byte is caught', () => {
    // Plant and catch, because a scanner nobody has watched fail is a scanner nobody knows works. Three
    // shapes: the NUL that happened, a backspace (the instance before it), and DEL.
    const dir = mkdtempSync(join(tmpdir(), 'byte-hygiene-'));
    try {
        for (const [name, byte] of [['nul.cs', 0x00], ['backspace.cs', 0x08], ['del.cs', 0x7f]]) {
            const file = join(dir, name);
            writeFileSync(file, Buffer.concat([Buffer.from('const char c = \''), Buffer.from([byte]), Buffer.from('\';\n')]));
            const hits = controlBytes(readFileSync(file));
            assert.equal(hits.length, 1, `${name}: the sweep did not see a planted 0x${byte.toString(16)}`);
            assert.equal(hits[0].byte, byte);
        }
        // the fourth shape: a LONE CR, which is legal-looking to a CRLF file and broke a rendered table
        // twice (F-085, and then the fix for F-085). Both positions matter: mid-line, and a bare \r line
        // ending that fuses two rows when the \n goes missing.
        const loneMid = join(dir, 'lone-mid.md');
        writeFileSync(loneMid, Buffer.from('| a |\rb |\r\n'));
        const midHits = controlBytes(readFileSync(loneMid));
        assert.equal(midHits.length, 1, 'a mid-line lone CR was not caught');
        assert.equal(midHits[0].lone, true);
        const loneEnd = join(dir, 'lone-end.md');
        writeFileSync(loneEnd, Buffer.from('| row one |\r| row two |\r\n'));
        assert.equal(controlBytes(readFileSync(loneEnd)).length, 1, 'a bare-CR line ending was not caught');
        // and it must not fire on the three bytes that are legal, CRLF included
        const clean = join(dir, 'clean.cs');
        writeFileSync(clean, 'line one\r\n\tindented\n');
        assert.equal(controlBytes(readFileSync(clean)).length, 0, 'the sweep fired on tab, LF or CRLF');
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

// F-064. The sweep was DESCRIBED as fail-closed and was not: every read failure became a silent skip, so a
// tracked file that exists and cannot be read is exempted without a word, and a control byte inside it is
// exempted with it. Exactly two read failures are legitimate skips, and both mean there is no file at that
// path: it is not in the working tree, or it is a submodule gitlink. Everything else is the sweep failing to
// do its job, and it has to say so. The scanned-count floor does not cover this: it is a vacuity check on
// the whole run, and one skipped file never moves it.
test('a tracked file that exists but cannot be read is a FAILURE, not a silent skip', () => {
    // The io is injected rather than staged on disk, because the case that matters is a real FILE that
    // readFileSync refuses, and ACLs are not reproducible across the three platforms CI runs on. What is
    // under test is the classification, and this exercises it directly and on every platform.
    const asFile = { isDirectory: () => false };
    const asDir = { isDirectory: () => true };
    const boom = (code) => () => { const e = new Error(code); e.code = code; throw e; };

    // a tracked FILE that cannot be read: FATAL, however it fails
    for (const code of ['EACCES', 'EPERM', 'EBUSY', 'EIO']) {
        const err = (() => {
            try { readTracked('/x/bad.cs', 'x/bad.cs', { lstat: () => asFile, readFile: boom(code) }); return null; }
            catch (e) { return e; }
        })();
        assert.ok(err, `an unreadable tracked file (${code}) was skipped instead of failing the sweep`);
        assert.match(err.message, /could not be read/);
        assert.match(err.message, /x\/bad\.cs/);
    }

    // the two legitimate skips, and ONLY these two
    assert.deepEqual(
        readTracked('/x/gone.cs', 'x/gone.cs', { lstat: boom('ENOENT'), readFile: boom('ENOENT') }),
        { skip: 'not in the working tree' });
    assert.deepEqual(
        readTracked('/x/sub', 'x/sub', { lstat: () => asDir, readFile: boom('EISDIR') }),
        { skip: 'a submodule gitlink, not a file' });

    // and the ordinary path still returns the bytes
    assert.deepEqual(
        readTracked('/x/ok.cs', 'x/ok.cs', { lstat: () => asFile, readFile: () => Buffer.from('ok') }),
        { buf: Buffer.from('ok') });
});

test('webview source does not ask the bundler to emit low control bytes', () => {
    const tracked = execFileSync('git', ['ls-files', '-z', 'Semanticus.VSCode/webview/src'], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 })
        .toString('utf8').split('\0').filter(Boolean);
    const escape = /\\(?:u00(?:0[0-9a-f]|1[0-9a-f])|x(?:0[0-9a-f]|1[0-9a-f]))/ig;
    const problems = [];
    for (const rel of tracked) {
        const text = readFileSync(resolve(repoRoot, rel), 'utf8');
        for (const hit of text.matchAll(escape))
            problems.push(`${rel}:${lineOf(Buffer.from(text.slice(0, hit.index), 'utf8'), hit.index)} contains ${hit[0]}`);
    }
    assert.deepEqual(problems, [],
        'low-control escapes in webview source are not safe: Vite/esbuild can minify them into raw bytes in media/studio. '
        + 'Use a printable key such as JSON.stringify([...]) instead:\n  ' + problems.join('\n  '));
});

test('no tracked text file contains a raw control byte', () => {
    const tracked = execFileSync('git', ['ls-files', '-z'], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 })
        .toString('utf8').split('\0').filter(Boolean);
    assert.ok(tracked.length > 100, `git ls-files returned only ${tracked.length} files; the sweep would be vacuous`);

    const problems = [];
    let scanned = 0;
    for (const rel of tracked) {
        if (BINARY_EXTENSIONS.has(extname(rel).toLowerCase())) continue;
        const read = readTracked(resolve(repoRoot, rel), rel);
        if (read.skip) continue;
        const buf = read.buf;
        scanned++;
        for (const hit of controlBytes(buf))
            problems.push(`${rel}:${lineOf(buf, hit.offset)} has raw byte 0x${hit.byte.toString(16).padStart(2, '0')} at offset ${hit.offset}`);
    }

    assert.ok(scanned > 100, `only ${scanned} files were scanned; the exclusion list has grown too far`);
    assert.deepEqual(problems, [],
        'raw control bytes in tracked source. These render as NOTHING in the file viewer, grep, sed and git diff, '
        + 'so the file looks correct in every review surface. Repair from a Python FILE with any backslash built '
        + 'from chr(92), never through a shell heredoc or a -c string, then verify byte-wise:\n  ' + problems.join('\n  '));
});
