// Zero-dialog authoring (the tree-program's final move): the FIVE DAX-bearing create commands make the object
// immediately with a generated name and open Monaco with the name as line 1 (a script-style header); Save applies
// name + DAX together. This file pins BOTH halves: the pure header parse/build round-trip (out/daxHeader.js) and
// the host wiring in src/extension.ts (create-then-edit, the header-aware virtual FS, lint/format) + the WHERE
// index shrink in help.tsx. Run: `npm test` (node, no VS Code host).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
    buildDaxHeader, parseDaxHeaderName, parseDaxHeader, splitDaxHeader, reKeyRef, uniqueName, refPartsOf,
    checkDaxHeader, decideDaxSave, decideRenameRecovery, guardModelMatch, identityToken,
} from '../out/daxHeader.js';
import { Buffer } from 'node:buffer';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
let passed = 0;
const test = (name, fn) => { fn(); passed++; console.log('  [PASS] ' + name); };

// --- 1. header build: kind-appropriate script grammar, keyed off the ref PREFIX -----------------------
test('buildDaxHeader emits the ratified grammar per kind', () => {
    assert.equal(buildDaxHeader('measure:Sales/New Measure'), "MEASURE 'Sales'[New Measure] =");
    assert.equal(buildDaxHeader('column:Sales/New Column'), "COLUMN 'Sales'[New Column] =");        // calc column ref prefix is "column:"
    assert.equal(buildDaxHeader('calcitem:Time/New Item'), "CALCULATIONITEM 'Time'[New Item] =");
    assert.equal(buildDaxHeader('function:NewFunction'), 'FUNCTION [NewFunction] =');
    assert.equal(buildDaxHeader('table:New Calculated Table'), "TABLE 'New Calculated Table' =");   // calc table
});
test('buildDaxHeader returns null for kinds with no name header', () => {
    assert.equal(buildDaxHeader('hierarchy:Sales/Geo'), null);
    assert.equal(buildDaxHeader('role:Reader'), null);
    assert.equal(buildDaxHeader('partition:Sales/Part1'), null);
});

// --- 2. parse: extract the (possibly edited) name; round-trips with build ------------------------------
test('parseDaxHeaderName reads the name back out of every header shape', () => {
    assert.equal(parseDaxHeaderName("MEASURE 'Sales'[New Measure] ="), 'New Measure');
    assert.equal(parseDaxHeaderName("COLUMN 'Sales'[New Column] ="), 'New Column');
    assert.equal(parseDaxHeaderName("CALCULATIONITEM 'Time'[YTD] ="), 'YTD');
    assert.equal(parseDaxHeaderName('FUNCTION [MyFunc] ='), 'MyFunc');
    assert.equal(parseDaxHeaderName("TABLE 'Bridge' ="), 'Bridge');
});
test('build -> parse round-trips the name for every headerable kind', () => {
    for (const ref of ['measure:Sales/Revenue', 'column:Sales/Flag', 'calcitem:Time/QTD', 'function:Fx', 'table:Calc']) {
        const name = refPartsOf(ref).name;
        assert.equal(parseDaxHeaderName(buildDaxHeader(ref)), name, ref);
    }
});
test('parseDaxHeaderName tolerates trailing whitespace / no space before =', () => {
    assert.equal(parseDaxHeaderName("MEASURE 'Sales'[X]="), 'X');
    assert.equal(parseDaxHeaderName("  MEASURE 'Sales'[X] =   "), 'X');
});
test('parseDaxHeaderName returns null when the header line is gone / not a header', () => {
    assert.equal(parseDaxHeaderName('CALCULATE([Revenue], ALL(Sales))'), null);
    assert.equal(parseDaxHeaderName(''), null);
    assert.equal(parseDaxHeaderName('// a comment'), null);
});
test('a renamed header parses to the NEW name (the create-then-edit rename signal)', () => {
    // user opens `MEASURE 'Sales'[New Measure] =` and edits the green name to Revenue
    assert.equal(parseDaxHeaderName("MEASURE 'Sales'[Revenue] ="), 'Revenue');
});
test('quotes-in-table and brackets-in-name survive the round-trip', () => {
    assert.equal(buildDaxHeader("measure:O'Brien/A]B"), "MEASURE 'O''Brien'[A]]B] =");
    assert.equal(parseDaxHeaderName("MEASURE 'O''Brien'[A]]B] ="), 'A]B');
});

// --- 3. split: header line vs DAX body ----------------------------------------------------------------
test('splitDaxHeader separates line 1 from the body', () => {
    const s = splitDaxHeader("MEASURE 'Sales'[X] =\n    CALCULATE([Revenue])\n");
    assert.equal(s.header, "MEASURE 'Sales'[X] =");
    assert.equal(s.body, '    CALCULATE([Revenue])\n');
    assert.equal(s.headerLen, "MEASURE 'Sales'[X] =\n".length);   // lint/format offset the body by exactly this
});
test('splitDaxHeader on a header-only doc yields an empty body', () => {
    const s = splitDaxHeader("MEASURE 'Sales'[X] =");
    assert.equal(s.body, '');
});

// --- 4. reKeyRef: the ref (identity == name) follows a header rename, slash-in-name safe ---------------
test('reKeyRef strips the whole known old name and appends the new (never splices a slash)', () => {
    assert.equal(reKeyRef('measure:Sales/New Measure', 'New Measure', 'Revenue'), 'measure:Sales/Revenue');
    assert.equal(reKeyRef('measure:Sales/Gross/Net', 'Gross/Net', 'Margin'), 'measure:Sales/Margin');
    assert.equal(reKeyRef('table:Calc', 'Calc', 'Bridge'), 'table:Bridge');
    assert.equal(reKeyRef('measure:Sales/X', 'Nope', 'Y'), 'measure:Sales/X');   // guard: no suffix match -> unchanged
});

// --- 4b. model-scoped slash names: table:/function: treat the WHOLE remainder as the name (CRITICAL 2) --
test('refPartsOf never slash-splits a model-scoped name (a calc table named "A/B")', () => {
    assert.deepEqual(refPartsOf('table:A/B'), { kind: 'table', name: 'A/B' });        // NOT { table:'A', name:'B' }
    assert.deepEqual(refPartsOf('function:Ns/Fn'), { kind: 'function', name: 'Ns/Fn' });
    assert.deepEqual(refPartsOf('measure:Sales/Gross/Net'), { kind: 'measure', table: 'Sales', name: 'Gross/Net' }); // scoped: first slash only
});
test('a slash-named calc table round-trips its header and re-keys correctly (no wrong-object collision)', () => {
    assert.equal(buildDaxHeader('table:A/B'), "TABLE 'A/B' =");                        // shows "A/B", not "B"
    assert.deepEqual(checkDaxHeader('table:A/B', "TABLE 'A/C' ="), { ok: true, name: 'A/C' });
    // reKeyRef strips the WHOLE known old name, so A/B -> A/C yields table:A/C (never table:A/A/C or a splice)
    assert.equal(reKeyRef('table:A/B', 'A/B', 'A/C'), 'table:A/C');
});

// --- 4c. checkDaxHeader: kind + container + non-empty name gate, else a fixable reason (CRITICAL 1) -----
test('checkDaxHeader accepts a well-formed header and returns the (possibly edited) name', () => {
    assert.deepEqual(checkDaxHeader('measure:Sales/New Measure', "MEASURE 'Sales'[Revenue] ="), { ok: true, name: 'Revenue' });
    assert.deepEqual(checkDaxHeader('function:Fx', 'FUNCTION [Gx] ='), { ok: true, name: 'Gx' });
    assert.deepEqual(checkDaxHeader('table:Calc', "TABLE 'Bridge' ="), { ok: true, name: 'Bridge' });
});
test('checkDaxHeader REJECTS a deleted / malformed header (body promoted to line 1)', () => {
    assert.equal(checkDaxHeader('measure:Sales/New Measure', 'RETURN x').ok, false);   // header deleted
    assert.equal(checkDaxHeader('measure:Sales/New Measure', 'CALCULATE([Revenue])').ok, false);
});
test('checkDaxHeader REJECTS a wrong keyword/kind (TABLE cannot rename a measure)', () => {
    assert.equal(checkDaxHeader('measure:Sales/New Measure', "TABLE 'Sales' =").ok, false);
});
test('checkDaxHeader REJECTS a container change (rename cannot move a measure to another table)', () => {
    assert.equal(checkDaxHeader('measure:Sales/New Measure', "MEASURE 'Other'[New Measure] =").ok, false);
});
test('checkDaxHeader REJECTS an empty name', () => {
    assert.equal(checkDaxHeader('measure:Sales/New Measure', "MEASURE 'Sales'[] =").ok, false);
});

// --- 4c-bis. CRITICAL 1: per-kind grammar — each kind has ONE canonical shape; wrong shapes DON'T validate --
test('checkDaxHeader REJECTS a wrong SHAPE for the kind (bracket vs quote is not interchangeable)', () => {
    // a calc table is TABLE 'Name' = ; the bracketed form is NOT a valid table header (would previously slip through)
    assert.equal(checkDaxHeader('table:Bridge', 'TABLE [Bridge] =').ok, false);
    // a function is FUNCTION [Name] = ; the quoted form is NOT a valid function header
    assert.equal(checkDaxHeader('function:Fx', "FUNCTION 'Fx' =").ok, false);
    // a measure REQUIRES its quoted container; a bare bracketed name is not a valid measure header
    assert.equal(checkDaxHeader('measure:Sales/M', 'MEASURE [M] =').ok, false);
});
test('checkDaxHeader REJECTS a name whose "]" is not doubled (bad bracket escaping)', () => {
    // MEASURE 'Sales'[A]B] = has a lone "]" — ambiguous/invalid; only [A]]B] (doubled) is a real name-with-bracket
    assert.equal(checkDaxHeader('measure:Sales/M', "MEASURE 'Sales'[A]B] =").ok, false);
    assert.deepEqual(checkDaxHeader('measure:Sales/M', "MEASURE 'Sales'[A]]B] ="), { ok: true, name: 'A]B' });
});
test('parseDaxHeader is keyword-driven: it only accepts each keyword\'s canonical shape', () => {
    assert.equal(parseDaxHeader('TABLE [Bridge] ='), null);       // TABLE wants a quote, not a bracket
    assert.equal(parseDaxHeader("FUNCTION 'Fx' ="), null);        // FUNCTION wants a bracket, not a quote
    assert.equal(parseDaxHeader('MEASURE [M] ='), null);          // MEASURE requires the quoted container
    assert.equal(parseDaxHeader("MEASURE 'Sales'[A]B] ="), null); // lone "]" — not a real header
    assert.equal(parseDaxHeader('WHATEVER [X] ='), null);         // an unknown keyword is never a header
});

// --- 4d. decideDaxSave: URI-borne identity (CRITICAL 2) — never content-adopted, cross-model refused --------
const HDR = (uriModelKey = 'm1', modelKey = 'm1') => ({ isHeaderUri: true, uriModelKey, modelKey });
test('decideDaxSave: NOT a header uri -> body (a tree-opened editor never parses line 1 as a header)', () => {
    // even when line 1 LOOKS like a header, a non-header uri stays body (content is never a promotion signal)
    assert.deepEqual(decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M] =", '', { isHeaderUri: false }), { kind: 'body' });
    assert.deepEqual(decideDaxSave('measure:Sales/M', 'CALCULATE([Revenue])', '', { isHeaderUri: false }), { kind: 'body' });
});
test('decideDaxSave: header uri + same model + valid line 1 -> header (rename+body)', () => {
    assert.deepEqual(decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M2] =", 'CALCULATE(1)', HDR()), { kind: 'header', name: 'M2' });
});
test('decideDaxSave: header uri survives a window reload (same model key) -> header', () => {
    // the in-memory registry is gone after reload, but the uri still carries hdr=1 + the SAME owning model key
    assert.deepEqual(decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M] =", '', HDR('m1', 'm1')), { kind: 'header', name: 'M' });
});
test('decideDaxSave: header uri from a DIFFERENT model -> reject (CRITICAL 2: no cross-model rename/write)', () => {
    const d = decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M] =", '', HDR('modelA', 'modelB'));
    assert.equal(d.kind, 'reject');
    assert.match(d.reason, /different model/i);
});
test('decideDaxSave: header uri + invalid line 1 -> reject (never a silent body-only save)', () => {
    const d = decideDaxSave('measure:Sales/M', 'RETURN x', 'RETURN x', HDR());
    assert.equal(d.kind, 'reject');
    assert.ok(typeof d.reason === 'string' && d.reason.length > 0);
});
test('decideDaxSave: header uri + a SECOND header in the body -> reject (CRITICAL 1: duplicate-header guard)', () => {
    const body = "MEASURE 'Sales'[M2] =\n    CALCULATE(1)";   // a duplicated header now sits in the DAX body
    const d = decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M] =", body, HDR());
    assert.equal(d.kind, 'reject');
    assert.match(d.reason, /second name header/i);
});
test('decideDaxSave: header uri + ordinary DAX body -> header (the guard does not false-fire)', () => {
    assert.deepEqual(decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M] =", 'VAR x = 1\nRETURN x', HDR()), { kind: 'header', name: 'M' });
});
test('decideDaxSave: a header uri missing EITHER identity key -> reject (CRITICAL 1: fail closed)', () => {
    // a header uri must ALWAYS carry identity and Save must know the live key; a missing key on either side is corrupt
    // and never falls open to a lenient body/line-1-only save that could touch the wrong model after an MCP-door swap.
    const both = decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M] =", '', { isHeaderUri: true });
    assert.equal(both.kind, 'reject');
    assert.match(both.reason, /identity is missing/i);
    assert.equal(decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M] =", '', { isHeaderUri: true, uriModelKey: 'm1' }).kind, 'reject');   // live key missing
    assert.equal(decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M] =", '', { isHeaderUri: true, modelKey: 'm1' }).kind, 'reject');      // uri key missing
});

// --- 4d-quater. guardModelMatch: the ONE identity gate shared by decideDaxSave + the host's per-RPC re-check (CRITICAL 1) --
test('guardModelMatch: equal keys ok; missing/cross-model reject with the honest copy', () => {
    assert.deepEqual(guardModelMatch('m1', 'm1'), { ok: true });
    const missingUri = guardModelMatch(undefined, 'm1');
    assert.equal(missingUri.ok, false);
    assert.match(missingUri.reason, /identity is missing/i);
    const missingLive = guardModelMatch('m1', undefined);
    assert.equal(missingLive.ok, false);
    assert.match(missingLive.reason, /identity is missing/i);
    const cross = guardModelMatch('modelA', 'modelB');
    assert.equal(cross.ok, false);
    assert.match(cross.reason, /different model/i);
});
test('guardModelMatch is the source of truth decideDaxSave reuses (same reasons)', () => {
    // the Save-time verdict and the per-RPC re-check must never diverge in wording or logic
    const cross = guardModelMatch('modelA', 'modelB');
    assert.equal(cross.ok, false);
    const viaSave = decideDaxSave('measure:Sales/M', "MEASURE 'Sales'[M] =", '', HDR('modelA', 'modelB'));
    assert.equal(viaSave.kind, 'reject');
    assert.equal(viaSave.reason, cross.reason);   // identical wording — one source of truth
});

// --- 4d-bis. identityToken: a stable, url-safe, COLLISION-RESISTANT model-key token (CRITICAL 2) -------------
test('identityToken is deterministic, url-safe, and separates distinct model keys', () => {
    assert.equal(identityToken('C:/models/Sales.pbip'), identityToken('C:/models/Sales.pbip'));   // stable
    assert.notEqual(identityToken('C:/models/Sales.pbip'), identityToken('C:/models/Other.pbip'));
    assert.match(identityToken('C:\\a\\b:c%d'), /^[A-Za-z0-9_-]+$/);   // base64url alphabet: no reserved/decode-sensitive uri chars
});
test('identityToken has NO findable collisions and round-trips the exact key (CRITICAL 2)', () => {
    // the old 32-bit FNV was collision-findable (~2^16 candidates); carrying the canonical identity removes the collision
    assert.notEqual(identityToken('C:/models/Sales.pbip'), identityToken('C:/models/sales.pbip'));   // case-adversarial
    assert.notEqual(identityToken('C:/a/b'), identityToken('C:/a\\b'));                                // separator-adversarial
    assert.notEqual(identityToken('C:/a/bc'), identityToken('C:/ab/c'));                               // shift-adversarial
    // the token carries the exact identity (reversible) and stays inside the decode-invariant base64url alphabet
    const path = 'C:\\Users\\k\\Sales model %.pbip';
    const t = identityToken(path);
    assert.match(t, /^[A-Za-z0-9_-]+$/, 'url-safe + invariant under a uri query decode round-trip');
    assert.equal(Buffer.from(t, 'base64url').toString('utf8'), path, 'round-trips back to the exact key');
    // a pathologically long key falls back to a 128-bit (32 hex) SHA-256 prefix rather than an unbounded token
    assert.match(identityToken('x'.repeat(5000)), /^[0-9a-f]{32}$/);
});

// --- 4d-ter. decideRenameRecovery: partial-rename recovery is IMMEDIATE-RETRY ONLY (CRITICAL 1 r8 / HIGH 2) ------------
// The record carries the session it was renamed in (model token + session id) + the revision the rename committed at.
// Refs are NAME-based, so an object living at the new name is NOT provably the one we renamed — a delete+recreate could
// have put an impostor there, and no probe can tell them apart. So the ONLY safe resume is same-session + revision
// UNCHANGED (nothing mutated since); a same-session revision drift (possible impostor) TOMBSTONES; a cross-session
// reject leaves the record untouched (HIGH 2 — never mutate off a cross-session compare).
const REC = (over = {}) => ({ newRef: 'measure:Sales/B', modelKey: 'm1', sessionId: 's1', revision: 7, ...over });
test('decideRenameRecovery: no record -> proceed off the old ref (normal flow)', () => {
    assert.deepEqual(decideRenameRecovery(undefined, 'measure:Sales/A', 'm1', 's1', 7), { kind: 'proceed', baseRef: 'measure:Sales/A' });
});
test('decideRenameRecovery: a record from a DIFFERENT model -> reject, never tombstone (its refs belong to another model)', () => {
    const d = decideRenameRecovery(REC({ modelKey: 'modelA' }), 'measure:Sales/A', 'modelB', 's1', 7);
    assert.equal(d.kind, 'reject');
    assert.match(d.reason, /different model/i);
    assert.notEqual(d.tombstone, true);
});
test('decideRenameRecovery: CRITICAL 1 — SAME session + revision UNCHANGED -> resume off the renamed ref (the one safe case)', () => {
    // Nothing has mutated since the rename committed, so the object still lives at newRef exactly as renamed; the caller
    // CAS-fences the write on record.revision so any interleaving mutation is still refused engine-side.
    assert.deepEqual(decideRenameRecovery(REC(), 'measure:Sales/A', 'm1', 's1', 7), { kind: 'proceed', baseRef: 'measure:Sales/B' });
});
test('decideRenameRecovery: CRITICAL 1 — SAME session but the revision MOVED (possible impostor) -> reject + TOMBSTONE', () => {
    // A->B committed at rev 7; the body failed and B was deleted + recreated as an UNRELATED object (rev moved to 8). No
    // probe can distinguish that impostor, so this can NEVER be safely resumed: TOMBSTONE (permanent, fail-closed refusal).
    const d = decideRenameRecovery(REC({ revision: 7 }), 'measure:Sales/A', 'm1', 's1', 8);
    assert.equal(d.kind, 'reject');
    assert.equal(d.tombstone, true);          // keep + mark dead, do NOT prune
    assert.notEqual(d.prune, true);           // the old prune signal is gone
    assert.match(d.reason, /re-apply your DAX/i);
});
test('decideRenameRecovery: HIGH 2 — a DIFFERENT session (reload/reopen) -> reject WITHOUT changing the record (no tombstone)', () => {
    // The same durable model token can reopen under a NEW session id; the immediate-retry chain is broken and identity
    // can't be vouched across sessions. Refuse, but NEVER mutate the record off a cross-session comparison — even when the
    // revision coincides. A clean close / sweep clears it.
    const d = decideRenameRecovery(REC({ sessionId: 's1', revision: 7 }), 'measure:Sales/A', 'm1', 's2', 7);
    assert.equal(d.kind, 'reject');
    assert.notEqual(d.tombstone, true);
    assert.match(d.reason, /re-apply your DAX/i);
});
test('decideRenameRecovery: CRITICAL 1 — a TOMBSTONE (state:dead) is a permanent fail-closed refusal regardless of session/revision', () => {
    // Once tombstoned, the record short-circuits to a permanent reject no matter what the live session/revision are — the
    // editor may never save again; only a clean close / sweep clears the record (host side).
    const tomb = REC({ state: 'dead' });
    for (const [sess, rev] of [['s1', 7], ['s1', 9], ['s2', 7], ['s2', 9]]) {
        const d = decideRenameRecovery(tomb, 'measure:Sales/A', 'm1', sess, rev);
        assert.equal(d.kind, 'reject', `${sess}/${rev} must still reject`);
        assert.equal(d.tombstone, true, `${sess}/${rev} must KEEP the tombstone`);
    }
    // Even a tombstone from ANOTHER model is never resumed — the wrong-model reject wins first (and never tombstones here).
    const foreign = decideRenameRecovery(REC({ state: 'dead', modelKey: 'modelA' }), 'measure:Sales/A', 'modelB', 's1', 7);
    assert.equal(foreign.kind, 'reject');
    assert.match(foreign.reason, /different model/i);
    assert.notEqual(foreign.tombstone, true);
});
test('decideRenameRecovery: an UNCONFIRMED live session/revision (transient read failure) -> reject, retry-when-connected, never tombstone', () => {
    const noSession = decideRenameRecovery(REC(), 'measure:Sales/A', 'm1', undefined, 7);
    assert.equal(noSession.kind, 'reject');
    assert.match(noSession.reason, /confirm|connected/i);
    assert.notEqual(noSession.tombstone, true);   // never mark dead on unconfirmed state
    const noRevision = decideRenameRecovery(REC(), 'measure:Sales/A', 'm1', 's1', undefined);
    assert.equal(noRevision.kind, 'reject');
    assert.match(noRevision.reason, /confirm|connected/i);
    assert.notEqual(noRevision.tombstone, true);
});
test('decideRenameRecovery: CRITICAL 1 two-save sequence — a stale write refuses, the record is retained as a tombstone, the second save ALSO refuses (the impostor at the new name is never written)', () => {
    // Save 1 (the stale write): A->B committed at rev 7 in session s1; the body write failed and B was deleted + recreated
    // as an UNRELATED impostor (rev moved to 8). The next save samples s1/8 and must REFUSE (never resume onto the impostor)
    // and TOMBSTONE the record — the very hole this round closes (previously the stored rev was trace-only and the impostor
    // was written successfully).
    const record = REC({ sessionId: 's1', revision: 7 });
    const first = decideRenameRecovery(record, 'measure:Sales/A', 'm1', 's1', 8);
    assert.equal(first.kind, 'reject', 'the stale write is refused (never resumed onto the impostor)');
    assert.equal(first.tombstone, true, 'the record is retained + marked dead, never pruned');
    // The host persists the tombstone (see extension.ts) and RETAINS the record; Save 2 re-evaluates the SAME dirty editor.
    const retained = { ...record, state: 'dead' };
    const second = decideRenameRecovery(retained, 'measure:Sales/A', 'm1', 's1', 8);
    assert.equal(second.kind, 'reject', 'the second save ALSO refuses (permanent)');
    assert.equal(second.tombstone, true);
    // Neither decision ever returned 'proceed', so no setDax ever targets the new name — the unrelated object at B is intact.
    assert.notEqual(first.kind, 'proceed');
    assert.notEqual(second.kind, 'proceed');
});

// --- 4e. parseDaxHeader exposes keyword + scope (the validation inputs) --------------------------------
test('parseDaxHeader returns keyword, scope, and name for each shape', () => {
    assert.deepEqual(parseDaxHeader("MEASURE 'Sales'[Revenue] ="), { keyword: 'MEASURE', table: 'Sales', name: 'Revenue' });
    assert.deepEqual(parseDaxHeader('FUNCTION [Fx] ='), { keyword: 'FUNCTION', name: 'Fx' });   // model-scope: no container key
    assert.deepEqual(parseDaxHeader("TABLE 'Bridge' ="), { keyword: 'TABLE', name: 'Bridge' });
    assert.equal(parseDaxHeader('not a header'), null);
});

// --- 5. uniqueName: collision-checked generated names -------------------------------------------------
test('uniqueName picks the first free "New X" / "New X 2" (case-insensitive)', () => {
    assert.equal(uniqueName('New Measure', new Set()), 'New Measure');
    assert.equal(uniqueName('New Measure', new Set(['new measure'])), 'New Measure 2');
    assert.equal(uniqueName('New Measure', new Set(['new measure', 'new measure 2'])), 'New Measure 3');
});
test('uniqueName uses a space-free suffix for UDFs (no spaces allowed)', () => {
    assert.equal(uniqueName('NewFunction', new Set(['newfunction']), ''), 'NewFunction2');
});

// ======================================================================================================
// Source-level wiring — the host side that the pure module can't prove on its own.
// ======================================================================================================
const ext = read('src/extension.ts');

// helper: the body of a top-level `async function <name>(` up to the next top-level `async function`/`function`.
function fnBody(src, name) {
    const start = src.indexOf('async function ' + name + '(');
    assert.ok(start >= 0, 'missing function ' + name);
    const after = src.indexOf('\nasync function ', start + 1);
    const after2 = src.indexOf('\nfunction ', start + 1);
    const end = Math.min(after < 0 ? src.length : after, after2 < 0 ? src.length : after2);
    return src.slice(start, end);
}

// --- 6. the header-aware virtual filesystem -----------------------------------------------------------
test('header-doc identity is URI-borne (hdr=1 + owning model key), not content (CRITICAL 2)', () => {
    // the header uri stamps identity onto the query; the plain uri stays body-only. isDaxHeaderDoc trusts the uri first.
    assert.match(ext, /function uriForHeaderRef\(ref: string\): vscode\.Uri/, 'a header-uri minter must exist');
    assert.match(ext, /\?hdr=1\$\{mk\}/, 'the header uri carries hdr=1 + the owning model key on its query');
    assert.match(ext, /function isHeaderUri\(uri: vscode\.Uri\): boolean/, 'the uri is the truth for header-doc identity');
    assert.match(ext, /function isDaxHeaderDoc\(uri: vscode\.Uri\): boolean \{ return isHeaderUri\(uri\) \|\| daxHeaderDocs\.has\(uri\.toString\(\)\); \}/, 'URI is truth, the Set is only a cache');
    assert.match(ext, /private withHeader\(uri: vscode\.Uri, body: string\)/, 'readFile/stat must share one header-prepend helper');
    assert.match(ext, /if \(!isDaxHeaderDoc\(uri\)\) return body;/, 'a non-header uri stays body-only (unchanged behavior)');
    assert.match(ext, /buildDaxHeader\(refFromUri\(uri\)\)/, 'the header is rebuilt from the ref, so it always reflects the current name');
});
test('save decides from the URI identity, rejects before any RPC, sequences rename-then-body honestly', () => {
    const wf = ext.slice(ext.indexOf('async writeFile('), ext.indexOf('watch(): vscode.Disposable'));
    assert.match(wf, /const \{ header, body \} = splitDaxHeader\(raw\)/, 'save must split the header from the body');
    // CRITICAL 2: the verdict folds URI-borne identity (isHeaderUri + owning model key) + line-1 validity + duplicate guard.
    assert.match(wf, /const decision = decideDaxSave\(oldRef, header, body, \{/, 'the verdict takes the header, the body (for the duplicate guard), and the URI identity');
    assert.match(wf, /isHeaderUri: isHeaderDoc/, 'header-doc-ness comes from the uri');
    assert.match(wf, /const uriModelKey = headerUriModelKey\(uri\)/, 'the owning model key comes from the uri query');
    assert.match(wf, /uriModelKey,/, 'the uri-borne owning model key is folded into the verdict');
    // CRITICAL 1: the live model identity is fetched AUTHORITATIVELY (one sessionInfo RPC) as token + session id.
    assert.match(wf, /const id = await this\.liveModelIdentity\(conn\); liveModelKey = id\.token; liveSession = id\.sessionId;/, 'the live model identity (durable token + live session id) is fetched from the engine at Save time');
    assert.match(wf, /modelKey: liveModelKey/, 'compared against the AUTHORITATIVE live model key, not the cached global');
    // CRITICAL 1: the swap race is closed ENGINE-side — every header-save mutation RPC carries expectedSession (liveSession),
    // so the engine refuses the write atomically inside its single-writer dispatch. The extension-only per-RPC re-check is gone.
    assert.doesNotMatch(wf, /assertLiveModelOwns/, 'the per-RPC extension re-check is replaced by the engine-side expectedSession fence');
    // The header-save path must NEVER fall open to a null fence — it fails closed if the live session id can't be confirmed.
    assert.match(wf, /if \(!liveSession\) throw new Error\(/, 'the header-save path fails closed when the live session id is missing (never passes null)');
    assert.doesNotMatch(wf, /liveSession \?\? null/, 'the header-save RPCs pass the session id itself, never a null fallback');
    const fences = (wf.match(/, 'human', liveSession,/g) || []).length;
    assert.ok(fences >= 3, 'expectedSession (liveSession) fences the rename AND every body-write RPC (>=3 call sites)');
    // CRITICAL (r7/r8): the expectedSession swap fence is blind to a SAME-session delete+recreate at a name; the header-save
    // VERIFIED-RESUMPTION writes also carry an expectedRevision so a mutation interleaving the rename and the write is
    // refused engine-side. The identity read carries the revision; a partial-rename resume proves it still matches the
    // rename's recorded revision, then CAS-fences the write on it.
    assert.match(wf, /liveRevision = id\.revision/, 'the live session revision is captured from the same sessionInfo read');
    assert.match(wf, /recoveryRevision = pending\.revision/, "a partial-rename resume fences the write on the rename's recorded revision (which the recovery proved still live)");
    // CRITICAL 1 (r8): the old liveness-probe resume is GONE — a probe can't vouch for identity across a delete+recreate, so
    // refExists / the getDax absent-marker were removed and the recovery no longer re-reads identity after any probe.
    assert.doesNotMatch(wf, /afterProbe/, 'the post-probe identity re-read is gone (no liveness probes remain)');
    assert.doesNotMatch(wf, /this\.refExists\(/, 'the recovery no longer probes ref liveness');
    assert.doesNotMatch(ext, /private async refExists\(/, 'the liveness-probe helper is removed');
    assert.doesNotMatch(ext, /GETDAX_ABSENT_MARKER/, 'the getDax absent-marker is removed with the probes');
    // the rename→body pair fences the BODY write on the RENAME'S returned revision (a delete+recreate between them refuses).
    assert.match(wf, /setDax', newRef, body, 'human', liveSession, renamed\.revision\)/, 'the body write after a rename is fenced on the rename\'s returned revision');
    // the recovery/plain body write passes recoveryRevision (set only on a resume; undefined => unfenced genuine plain save).
    assert.match(wf, /setDax', baseRef, body, 'human', liveSession, recoveryRevision\)/, 'the recovery body write is fenced on the recorded revision; a genuine plain save passes undefined (unfenced — SCOPE RULING)');
    // SCOPE RULING: a genuine plain save (no pending) must NOT be revision-fenced — recoveryRevision is left undefined there.
    assert.match(wf, /SCOPE RULING/, 'the plain-save scope ruling is documented at the call site');
    // CRITICAL 1 (r8): a confirmed post-save model drift is still surfaced as a backstop after the writes land.
    assert.match(wf, /warnIfModelDrifted\(conn, uriModelKey\)/, 'a confirmed post-save model drift is still surfaced as a backstop after the writes land');
    // CRITICAL 1/2: any reject (missing/wrong model / malformed / duplicate header) throws BEFORE any RPC (throw = doc stays dirty).
    assert.match(wf, /if \(decision\.kind === 'reject'\)[\s\S]*?throw new Error\(decision\.reason\)/, 'a rejected save never touches the engine');
    // HIGH 3 / CRITICAL 1 (r8): a prior partial rename is recovered from the PERSISTED record, and the pure recovery decides
    // the resume from the LIVE session + revision (the immediate-retry proof) — never a blind retry, never a liveness probe.
    assert.match(wf, /const pending = getPendingRename\(key\)/, 'a prior committed-rename-but-failed-body is recovered from persisted state');
    assert.match(wf, /const recovery = decideRenameRecovery\(pending, oldRef, liveModelKey, liveSession, liveRevision\)/, 'the pure recovery decides the resume from the live session + revision (or rejects)');
    // HIGH 3 / CRITICAL 2: a reject throws (doc stays dirty). A same-session revision-drift record is TOMBSTONED
    // (state:'dead') so a still-dirty editor can never later save against an impostor recreated at the new name.
    assert.match(wf, /if \(recovery\.kind === 'reject'\) \{[\s\S]*?if \(recovery\.tombstone && pending\.state !== 'dead'\) await setPendingRename\(key, \{ \.\.\.pending, state: 'dead' \}\)[\s\S]*?throw new Error\(recovery\.reason\)/, 'an inconsistent / cross-model / vanished recovery throws (doc stays dirty); a vanished-target record is tombstoned, never pruned');
    assert.doesNotMatch(wf, /if \(recovery\.prune\) await clearPendingRename/, 'the unsafe auto-prune is gone (a dirty doc must never lose its guard)');
    assert.match(wf, /baseRef = recovery\.baseRef/, 'the object\'s REAL current ref comes from the recovery decision');
    // HIGH 3: rename FIRST off baseRef; a refusal throws (no body written to the OLD object, doc stays dirty).
    assert.match(wf, /renamed = await conn\.sendRequest<RenameResult>\('renameObject', baseRef, newName, 'human', liveSession, recoveryRevision\)/, 'the rename is attempted first off the real current ref, fenced by expectedSession (+ the recorded revision on a recovery resume)');
    assert.match(wf, /Could not rename to/, 'a rename refusal is surfaced by throwing (nothing saved, doc dirty)');
    // CRITICAL 2: re-key to the ENGINE-returned newRef (authoritative), reKeyRef only as a fallback.
    assert.match(wf, /const newRef = renamed\?\.newRef \? renamed\.newRef : reKeyRef\(baseRef, oldName, newName\)/, 'the new ref comes from the engine, not a slash-splice');
    assert.match(wf, /setDax', newRef, body/, 'the body is set on the RENAMED ref (never the stale one)');
    // HIGH 3 / CRITICAL 1 (r8): rename-ok-but-setDax-fail PERSISTS the new ref + the SESSION (model token + session id) + the
    // rename revision — the immediate-retry resume fence, not mere provenance; DAX kept in place.
    assert.match(wf, /await setPendingRename\(key, \{ newRef, modelKey: liveModelKey \?\? '', sessionId: liveSession, revision: renamed\.revision \}\)/, 'a partial (rename ok, body fail) persists the new ref + session (model token + session id) + the rename revision (the resume fence)');
    assert.match(wf, /Your DAX is kept here\. Fix it and Save again\./, 'the partial-failure message promises the DAX is preserved in place');
    assert.doesNotMatch(wf, /await reopenRenamedDaxNow\(uri, newRef\)/, 'a partial failure NEVER closes/re-homes the dirty editor (HIGH 3)');
    assert.match(wf, /reopenRenamedDax\(uri, newRef\)/, 'the CLEAN path re-homes the editor onto the renamed object');
    assert.match(wf, /reopenRenamedDax\(uri, baseRef\)/, 'a resolved pending rename finally re-homes the (now clean) editor');
    // the success toast claims only what happened — no "applied together".
    assert.match(wf, /Renamed to "\$\{newName\}" and saved\./, 'the success toast names the rename honestly');
    assert.doesNotMatch(wf, /applied together/, 'the "applied together" claim is gone (two tracked changes)');
});
test('lint surfaces a broken header on line 1; lint + Format decide by URI identity (HIGH 4)', () => {
    // HIGH 4: header-doc-ness is URI-decided (isDaxHeaderDoc), and an INVALID header gets an explicit line-1 diagnostic.
    assert.match(ext, /if \(isDaxHeaderDoc\(doc\.uri\)\) \{\s*const s = splitDaxHeader\(text\);\s*const chk = checkDaxHeader\(refFromUri\(doc\.uri\), s\.header\);/, 'the linter decides by uri identity and checks the header');
    assert.match(ext, /if \(!chk\.ok\) \{[\s\S]*?vscode\.DiagnosticSeverity\.Error\)/, 'a broken header raises a visible ERROR on line 1');
    assert.match(ext, /doc\.positionAt\(d\.start \+ base\)/, 'diagnostic offsets shift back past the header');
    const fmt = ext.slice(ext.indexOf('const daxFormatProvider'), ext.indexOf('const daxRangeFormatProvider'));
    assert.match(fmt, /if \(isDaxHeaderDoc\(doc\.uri\)\)/, 'Format Document decides by uri identity');
    assert.match(fmt, /if \(checkDaxHeader\(refFromUri\(doc\.uri\), header\)\.ok\)/, 'Format formats only the body when line 1 still validates as the header');
    assert.match(fmt, /new vscode\.Position\(1, 0\)/, 'the format edit starts at line 1 (the header on line 0 is preserved)');
});
test('the in-memory cache is cleared on model swap; PERSISTED pending renames survive it and reloads (HIGH 3)', () => {
    assert.match(ext, /function clearDaxHeaderDocsOnSwap\(sessionId\?: string\)/, 'a session-swap clear must exist');
    // HIGH 3: a swap wipes ONLY the in-memory header cache; pending renames are persisted so they survive a reload
    // (which itself looks like a swap) — a stale record is guarded by the model-token check in decideRenameRecovery.
    assert.match(ext, /daxHeaderDocs\.clear\(\);/, 'a swap wipes the in-memory header cache');
    assert.doesNotMatch(ext, /daxPendingRename\.clear\(\)/, 'a swap must NOT wipe persisted pending renames (they survive a reload)');
    // pending renames live in workspaceState (persisted, workspace-scoped), not a per-process Map.
    assert.match(ext, /extCtx\?\.workspaceState\.get<Record<string, PendingRenameRecord>>\(DAX_PENDING_RENAME_KEY\)/, 'pending renames are read from workspaceState');
    assert.match(ext, /extCtx\?\.workspaceState\.update\(DAX_PENDING_RENAME_KEY,/, 'pending renames are persisted to workspaceState');
    assert.match(ext, /clearDaxHeaderDocsOnSwap\(info\?\.sessionId\)/, 'setStatusFromInfo clears on session-identity change');
    assert.match(ext, /daxHeaderModelKey = modelKeyOf\(info\)/, 'the current model key is the source path (reload-stable), else the session id');
    assert.match(ext, /function modelKeyOf\(info\?: SessionInfo\): string \| undefined \{ return info\?\.source \|\| info\?\.sessionId; \}/, 'one helper defines the model-identity string for every caller');
    // HIGH 3 / CRITICAL 2: a doc close always prunes the in-memory caches. The PERSISTED pending-rename record (incl. a
    // tombstone) is cleared ONLY on a CLEAN close (!isDirty): a reload / hot-exit restores the doc still dirty (the
    // lifecycle the record must survive), so a dirty close keeps it; a clean close means the DAX was discarded/saved and
    // no dirty editor can still resume, so it is safe to drop.
    assert.match(ext, /if \(!d\.isDirty\) void clearPendingRename\(d\.uri\.toString\(\)\)/, 'a CLEAN close clears the persisted record (incl. a tombstone); a dirty close keeps it (survives a reload)');
    // CRITICAL 2: an orphan record whose doc closed while the extension was inactive is dropped by the activation sweep —
    // which only clears records with NO open tab AND no open document (a restored dirty doc is KEPT; fail-closed).
    assert.match(ext, /async function sweepClosedPendingRenames\(\)/, 'an activation sweep drops records whose editor is no longer open');
    assert.match(ext, /for \(const key of keys\) \{ if \(!open\.has\(key\)\) await clearPendingRename\(key\); \}/, 'the sweep clears only records with no open tab/document (keeps a restored dirty doc)');
    assert.match(ext, /setTimeout\(\(\) => \{ void sweepClosedPendingRenames\(\); \}, \d+\)/, 'the sweep runs at activation, deferred so restored tabs/docs are present first');
});

// --- 7. create-then-edit: the five flows create immediately, open the editor, NO name InputBox --------
test('createThenEdit creates, reveals+selects in the tree, then opens the header editor', () => {
    const c = fnBody(ext, 'createThenEdit');
    assert.match(c, /await revealRefInTree\(ref\)/, 'the created object must appear selected in the Model tree');
    // CRITICAL 1: sample identity BEFORE the create and re-verify UNCHANGED after; a swap under the create aborts the open.
    assert.match(c, /const beforeKey = modelKeyOf\(await refreshStatus\(\)\);/, 'identity is sampled (RPC) BEFORE the create');
    assert.match(c, /const afterKey = modelKeyOf\(await refreshStatus\(\)\);/, 'identity is re-sampled (RPC) after the create');
    // MEDIUM 4: guardModelMatch requires BOTH samples present AND equal, so two MISSING samples no longer pass an
    // undefined===undefined check and stamp a header editor from the stale cached global.
    assert.match(c, /if \(!guardModelMatch\(beforeKey, afterKey\)\.ok\) \{[\s\S]*?showWarningMessage[\s\S]*?return ref;/, 'a model swap OR an unconfirmable identity under the create warns + aborts the header-editor open (no foreign-model stamp)');
    assert.match(c, /const hUri = uriForHeaderRef\(ref\);/, 'the editor opens on a HEADER uri (identity stamped on the uri)');
    assert.match(c, /daxHeaderDocs\.add\(hUri\.toString\(\)\)/, 'the header-uri is cached BEFORE open so the header shows');
    assert.match(c, /await openDaxAt\(hUri, ref\)/, 'the editor opens on the new object\'s header uri');
    assert.match(c, /Created \$\{label\}/, 'a status message says what was created (the honesty rail)');
});
for (const [fn, method] of [
    ['authorNewMeasure', 'createMeasure'],
    ['authorNewCalcColumn', 'createCalculatedColumn'],
    ['authorNewCalcTable', 'createCalculatedTable'],
    ['authorNewCalcItem', 'createCalculationItem'],
    ['authorNewFunction', 'createFunction'],
]) {
    test(`${fn} is zero-dialog: create-then-edit, generated name, no name InputBox`, () => {
        const body = fnBody(ext, fn);
        assert.match(body, /createThenEdit\(/, `${fn} must use the create-then-edit path`);
        assert.match(body, new RegExp(`'${method}'`), `${fn} must call ${method}`);
        assert.match(body, /uniqueName\(/, `${fn} must generate a collision-checked name`);
        assert.doesNotMatch(body, /showInputBox/, `${fn} must NOT prompt for a name in a floating box`);
    });
}
test('measure/calc-column/calc-item resolve their container from context, one picker only without it', () => {
    // behavior #2: a tree right-click uses the node; the palette path infers from selection, else ONE picker.
    assert.match(ext, /async function resolveContainer\(n: TreeNode \| undefined, want: 'table' \| 'calcgroup'/, 'a shared single-picker container resolver must exist');
    assert.doesNotMatch(fnBody(ext, 'authorNewCalcColumn'), /showInputBox/, 'calc column takes no InputBox');
    assert.doesNotMatch(fnBody(ext, 'authorNewCalcItem'), /showInputBox/, 'calc item takes no InputBox');
});

// --- 8. collapsed chains: relationship = one picker; folder = one InputBox ------------------------------
test('New Relationship is one QuickPick of candidate lookup columns (no free-text ref box)', () => {
    const r = fnBody(ext, 'authorNewRelationship');
    assert.doesNotMatch(r, /showInputBox/, 'the free-text "column:Table/Col" box is gone');
    assert.match(r, /showQuickPick/, 'candidates are chosen from a picker');
    assert.match(r, /createRelationship/, 'the pick completes the relationship');
});
test('New Folder is a single InputBox (name only) that seeds its first measure via create-then-edit', () => {
    const f = fnBody(ext, 'newFolderCmd');
    const boxes = (f.match(/showInputBox/g) || []).length;
    assert.equal(boxes, 1, 'exactly one InputBox (the folder name)');
    assert.doesNotMatch(f, /showQuickPick/, 'the old "how to fill it" QuickPick is gone');
    assert.match(f, /createThenEdit\('createMeasure'/, 'the folder is seeded by its first measure (create-then-edit)');
});

// --- 9. the WHERE index shrank (the metric): obsolete entries deleted ----------------------------------
test('the help WHERE index folds the zero-dialog creates but keeps hierarchy + description honest', () => {
    const help = read('webview/src/help.tsx');
    const block = help.slice(help.indexOf('const WHERE:'), help.indexOf('\n];', help.indexOf('const WHERE:')));
    const count = (block.match(/\{ q: '/g) || []).length;
    // MEDIUM 5 — ADJUDICATED (coordinator): 39 entries, NOT the 37 an early note cited. Honesty wins: the two entries
    // 'Create a hierarchy' and 'Set a description or display folder' are honest RESTORATIONS (real tasks with a real
    // home) — folding them into the DAX-editor claim would have been false. The "index shrank" metric is measured across
    // the whole tree program, not this PR, so net task-coverage is unchanged. No deletion.
    assert.equal(count, 39, 'the index is 39 entries (2 honest restorations kept; see MEDIUM 5 adjudication above)');
    assert.doesNotMatch(block, /q: 'Create a measure',/, 'the standalone "Create a measure" entry is gone (one gesture, self-evident)');
    assert.match(block, /Create a measure, calculated column, calculated table, calculation item, or function/, 'a single create entry covers the five zero-dialog kinds');
    // MEDIUM 6: hierarchy creation (NOT zero-dialog) keeps its own honest entry rather than being folded into the
    // DAX-editor claim; the description/display-folder task is restored, pointed at Properties via the selection.
    assert.match(block, /q: 'Create a hierarchy'/, 'hierarchy creation keeps an honest entry (it uses dialogs, not the DAX header)');
    assert.match(block, /q: 'Set a description or display folder'/, 'the description/display-folder task has a home again, via the Properties view');
    // HIGH 3 / MEDIUM 6: the create entry no longer claims the rename + DAX are "applied together".
    assert.doesNotMatch(block, /applies both/, 'the "applies both" wording is gone (two tracked changes, rename first)');
    assert.match(block, /two tracked changes/, 'the create entry is honest that a rename and the DAX are two changes');
    assert.match(block, /F2 to rename in the Properties Name row/, 'rename now points at F2 / the Properties Name row');
});
test('resolveContainer infers the calc group from a selected calculation ITEM (MEDIUM 5)', () => {
    const rc = fnBody(ext, 'resolveContainer');
    assert.match(rc, /want !== 'calcgroup' \|\| x\?\.kind !== 'calcitem'/, 'a calc item identifies its group (no picker needed)');
    assert.match(rc, /'table:' \+ g, name: g, kind: 'calcgroup'/, 'the inferred group node uses the table: ref prefix a calc group carries');
});
test('createThenEdit splits create from open: a created-but-editor-failed object is NOT reported as "Could not create" (MEDIUM 7)', () => {
    const c = fnBody(ext, 'createThenEdit');
    // the create is in its own try; only its failure says "Could not create"
    assert.match(c, /catch \(e: any\) \{\s*vscode\.window\.showErrorMessage\(`Could not create \$\{label\}: `/, 'only a failed CREATE reports "Could not create"');
    assert.match(c, /but its editor didn't open/, 'a create that succeeded but whose editor failed is reported separately');
});

console.log(`\ndax-header.test.mjs: ${passed} passed`);
