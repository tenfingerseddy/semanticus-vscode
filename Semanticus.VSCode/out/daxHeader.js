"use strict";
// Zero-dialog authoring: the script-style NAME HEADER that leads a freshly-created object's DAX editor, so
// authoring is one motion (create -> name in line 1 -> body below -> Save) instead of a name InputBox before
// Monaco. Pure + host-free so it unit-tests at the source level (test/dax-header.test.mjs imports out/daxHeader.js).
//
// The header grammar is display-faithful to the ratified demo (MEASURE 'Sales'[New Measure] =) and TMDL's object
// keywords. It is NOT the engine's apply_dax_script "// @object <ref>" sentinel: that grammar carries a ref (which
// re-encodes the name) and never renames, so the header is parsed HERE and a name change routes through the engine
// rename seam on the host side. Only the FIVE create-then-edit kinds get a header; everything else is body-only.
Object.defineProperty(exports, "__esModule", { value: true });
exports.MODEL_SCOPED_KINDS = void 0;
exports.refPartsOf = refPartsOf;
exports.buildDaxHeader = buildDaxHeader;
exports.headerKeywordFor = headerKeywordFor;
exports.parseDaxHeader = parseDaxHeader;
exports.parseDaxHeaderName = parseDaxHeaderName;
exports.checkDaxHeader = checkDaxHeader;
exports.guardModelMatch = guardModelMatch;
exports.decideDaxSave = decideDaxSave;
exports.decideRenameRecovery = decideRenameRecovery;
exports.identityToken = identityToken;
exports.splitDaxHeader = splitDaxHeader;
exports.reKeyRef = reKeyRef;
exports.uniqueName = uniqueName;
const node_buffer_1 = require("node:buffer");
const node_crypto_1 = require("node:crypto");
// Kinds whose ref carries NO container: the whole remainder after 'kind:' is the name, slashes and all (a calc table
// or UDF can be named "A/B"). A container-scoped kind (measure/column/calcitem/...) uses the FIRST slash to split
// container from name. Getting this wrong is CRITICAL: slash-splitting `table:A/B` would yield the name "B" and, on
// rename, re-key to a DIFFERENT existing object. Kept in sync with extension.ts's refParts.
exports.MODEL_SCOPED_KINDS = new Set(['table', 'function', 'calcgroup', 'role', 'perspective', 'model']);
/** 'column:Sales/Amount' -> { kind:'column', table:'Sales', name:'Amount' }. Standalone copy (no vscode import). */
function refPartsOf(ref) {
    const colon = ref.indexOf(':');
    const kind = colon >= 0 ? ref.slice(0, colon) : '';
    const rest = colon >= 0 ? ref.slice(colon + 1) : ref;
    if (exports.MODEL_SCOPED_KINDS.has(kind))
        return { kind, name: rest }; // whole remainder is the name (may contain '/')
    const slash = rest.indexOf('/');
    if (slash < 0)
        return { kind, name: rest };
    return { kind, table: rest.slice(0, slash), name: rest.slice(slash + 1) };
}
// Quote a table/table-scope segment TMDL-style: single quotes, embedded quotes doubled.
function q(s) { return "'" + s.replace(/'/g, "''") + "'"; }
// Bracketed name: brackets doubled ("]" -> "]]"), so the closing "]" of a name-with-bracket is unambiguous.
function br(s) { return '[' + s.replace(/]/g, ']]') + ']'; }
function unbr(s) { return s.replace(/]]/g, ']'); }
/**
 * The header line for a ref, or null when the ref's kind carries no name-header (only the create-then-edit kinds do).
 * Keyed off the ref PREFIX (a calc column's ref is "column:", a calc table's is "table:") so readFile can rebuild it
 * from the uri alone. The trailing " =" mirrors a script definition; the object name is the editable payload.
 */
function buildDaxHeader(ref) {
    const { kind, table, name } = refPartsOf(ref);
    switch (kind) {
        case 'measure': return `MEASURE ${q(table ?? '')}${br(name)} =`;
        case 'column': return `COLUMN ${q(table ?? '')}${br(name)} =`; // calc columns only reach here (data columns carry no DAX editor)
        case 'calcitem': return `CALCULATIONITEM ${q(table ?? '')}${br(name)} =`; // "table" is the owning calculation group
        case 'function': return `FUNCTION ${br(name)} =`; // model-scope, no table
        case 'table': return `TABLE ${q(name)} =`; // a calculated table
        default: return null;
    }
}
/** The TMDL header keyword for a create-then-edit ref kind (else null). Kept beside buildDaxHeader so the two agree. */
function headerKeywordFor(kind) {
    switch (kind) {
        case 'measure': return 'MEASURE';
        case 'column': return 'COLUMN';
        case 'calcitem': return 'CALCULATIONITEM';
        case 'function': return 'FUNCTION';
        case 'table': return 'TABLE';
        default: return null;
    }
}
/** A human label for a kind, used only in honest error copy. */
function kindLabel(kind) {
    switch (kind) {
        case 'measure': return 'measure';
        case 'column': return 'calculated column';
        case 'calcitem': return 'calculation item';
        case 'function': return 'function';
        case 'table': return 'calculated table';
        default: return 'object';
    }
}
// The two escaped-segment grammars, enforced literally: a quoted segment may only contain a "'" as part of a doubled
// "''" pair; a bracketed name may only contain a "]" as part of a doubled "]]" pair. Using these (not a greedy `.*`)
// is what REJECTS bad escaping like `[A]B]` (a lone "]") -- the closing bracket would otherwise be ambiguous. CRITICAL.
const Q_INNER = "(?:[^']|'')*"; // 'quoted' body
const BR_INNER = "(?:[^\\]]|\\]\\])*"; // [bracketed] body
/**
 * Parse a header line into its keyword, optional container scope, and name -- or null when line 1 is not a recognizable
 * header for THAT keyword. Per-kind grammar: each keyword has exactly ONE canonical shape, and the shape is enforced
 * (not accepted generically), so `TABLE [X] =` (bracket, not quote), `FUNCTION 'X' =` (quote, not bracket) and
 * `MEASURE 'S'[A]B] =` (unescaped "]") all fail to parse. Richer than parseDaxHeaderName (name only): the keyword +
 * scope let the host VALIDATE the header against the object's kind + container before it renames anything.
 *   measure / column / calcitem -> KEYWORD 'Container'[Name] =   (quoted container REQUIRED + bracketed name)
 *   function                    -> FUNCTION [Name] =             (bracketed name, no container)
 *   table                       -> TABLE 'Name' =                (quoted name, no bracket)
 */
function parseDaxHeader(line) {
    const t = line.trim();
    const m = /^([A-Z]+)\s+([\s\S]*?)\s*=\s*$/.exec(t); // KEYWORD <shape> = ; the trailing '=' is the terminator
    if (!m)
        return null;
    const keyword = m[1];
    const mid = m[2];
    switch (keyword) {
        case 'MEASURE':
        case 'COLUMN':
        case 'CALCULATIONITEM': {
            const g = new RegExp(`^'(${Q_INNER})'\\s*\\[(${BR_INNER})\\]$`).exec(mid);
            return g ? { keyword, table: g[1].replace(/''/g, "'"), name: unbr(g[2]) } : null;
        }
        case 'FUNCTION': {
            const g = new RegExp(`^\\[(${BR_INNER})\\]$`).exec(mid);
            return g ? { keyword, name: unbr(g[1]) } : null;
        }
        case 'TABLE': {
            const g = new RegExp(`^'(${Q_INNER})'$`).exec(mid);
            return g ? { keyword, name: g[1].replace(/''/g, "'") } : null;
        }
        default: return null; // an unrecognized keyword (VAR, RETURN, CALCULATE, ...) is never a header
    }
}
/**
 * Extract the (possibly edited) object name from a header line, or null when the line is not a recognizable header
 * (e.g. the user deleted it). Thin wrapper over parseDaxHeader for callers that only need the name.
 */
function parseDaxHeaderName(line) {
    return parseDaxHeader(line)?.name ?? null;
}
/**
 * Validate line 1 as the name header for THIS object: it must parse (per-kind grammar + escaping), its keyword must
 * match the ref's kind, its container ('Table' / calc group) must match the ref's, and the name must be non-empty. The
 * rename seam changes an object's NAME only -- never its kind or container -- so a header that disagrees on either is
 * REJECTED (never silently applied as a body-only save, which would drop the header and lose DAX). Returns the name, or
 * a fixable reason. NB the name is `checkDaxHeader`, not `validate*` -- the coverage oracle's substring match would
 * otherwise pin this file as false evidence for the MCP `validate_dax` op (MEDIUM 6).
 */
function checkDaxHeader(ref, line) {
    const { kind, table } = refPartsOf(ref);
    const kw = headerKeywordFor(kind);
    if (!kw)
        return { ok: false, reason: 'This object has no name header.' };
    const label = kindLabel(kind);
    const parsed = parseDaxHeader(line);
    if (!parsed)
        return { ok: false, reason: `Line 1 must stay the name header (${buildDaxHeader(ref)}). Restore it, then Save.` };
    if (parsed.keyword !== kw)
        return { ok: false, reason: `Line 1 must start with ${kw} for this ${label} (found ${parsed.keyword}). Fix line 1, then Save.` };
    const scoped = kind === 'measure' || kind === 'column' || kind === 'calcitem';
    if (scoped) {
        if ((parsed.table ?? '') !== (table ?? '')) {
            const where = kind === 'calcitem' ? 'calculation group' : 'table';
            return { ok: false, reason: `Line 1's ${where} must stay '${table ?? ''}' (found '${parsed.table ?? ''}'). Renaming can't move a ${label} to another ${where}.` };
        }
    }
    else if (parsed.table !== undefined) {
        return { ok: false, reason: `Line 1 must be ${buildDaxHeader(ref)} (no table scope). Fix line 1, then Save.` };
    }
    if (!parsed.name)
        return { ok: false, reason: `Give the ${label} a name in line 1 before Save.` };
    return { ok: true, name: parsed.name };
}
/**
 * The identity gate shared by decideDaxSave AND the host's per-RPC re-verification (CRITICAL 1). A header uri must
 * ALWAYS carry its owning-model token and the caller must know the LIVE model's token; a missing token on either side
 * is CORRUPT (never falls open to a lenient save that could touch the wrong model after an MCP-door swap), and the two
 * tokens must be EQUAL for a mutation to target the intended model. One source of truth so the Save-time verdict and the
 * immediately-before-each-mutation re-check can never drift apart in wording or in logic.
 */
function guardModelMatch(uriModelKey, liveModelKey) {
    if (uriModelKey === undefined || liveModelKey === undefined) {
        return { ok: false, reason: "This editor's model identity is missing, so the object it belongs to can't be confirmed. Reopen it from the Model tree, then Save." };
    }
    if (uriModelKey !== liveModelKey) {
        return { ok: false, reason: 'This editor belongs to a different model session. Reopen the object from the current model, then Save.' };
    }
    return { ok: true };
}
/**
 * Decide how to save a DAX document from its URI-borne identity (never its content). Pure, so the reload / model-swap
 * decisions are unit-testable without a VS Code host.
 *   NOT a header uri                          -> body (a tree-opened editor; line 1 is NEVER parsed as a header)
 *   header uri, identity missing (uri or live) -> reject (a header uri must always carry identity; absent = corrupt)
 *   header uri, different owning model         -> reject (a header doc from another model must not touch this one)
 *   header uri, same model, line 1 invalid     -> reject (never silently drop a malformed / deleted / wrong-kind header)
 *   header uri, same model, a 2nd header below -> reject (a duplicated header would otherwise be stored as DAX)
 *   header uri, same model, line 1 valid       -> header (rename the object if the name changed, then set the body)
 */
function decideDaxSave(ref, header, body, id) {
    if (!id.isHeaderUri)
        return { kind: 'body' };
    // CRITICAL 1 -- fail CLOSED on identity. A header uri must ALWAYS carry its owning-model token, and Save must know the
    // LIVE model's token; a missing token on either side is CORRUPT and never falls open to a body/line-1-only save that
    // could touch the wrong model (e.g. the live key was compared against a cached global that never refreshed after an
    // MCP-door model swap). Only an exact uri-vs-live match may proceed. NB this verdict is sampled ONCE; the host
    // re-runs guardModelMatch immediately before EACH mutation RPC to narrow the swap window (see writeFile / CRITICAL 1).
    const match = guardModelMatch(id.uriModelKey, id.modelKey);
    if (!match.ok)
        return { kind: 'reject', reason: match.reason };
    const v = checkDaxHeader(ref, header);
    if (!v.ok)
        return { kind: 'reject', reason: v.reason };
    // Duplicate-header guard: the body's FIRST non-empty line must not itself parse as a header, else a second header
    // (e.g. a pasted-in or mis-duplicated one) would be stored as part of the DAX with Verified Mode off. CRITICAL 1.
    const stray = firstNonEmptyLine(body);
    if (stray !== null && parseDaxHeader(stray) !== null) {
        return { kind: 'reject', reason: 'The DAX below line 1 starts with a second name header. Keep only the line-1 header, then Save.' };
    }
    return { kind: 'header', name: v.name };
}
/** The first line with non-whitespace content, or null when the text is blank. */
function firstNonEmptyLine(text) {
    for (const line of text.split('\n')) {
        if (line.trim().length > 0)
            return line;
    }
    return null;
}
/**
 * Decide the write target for a header-doc Save in the presence of a possibly-persisted partial-rename record (HIGH 3).
 * A prior Save may have COMMITTED a rename but FAILED to write the body, leaving a dirty editor on the stale old ref and a
 * persisted record of the ref the object was renamed to. Pure, so every branch is unit-testable without a VS Code host.
 *
 * CRITICAL 1 (round 8) -- refs are NAME-based, so an object living at the new name is NOT proof it is the one we renamed:
 * after A->B commits and B is deleted, an unrelated object can be RE-CREATED at B, and no liveness probe can distinguish
 * that impostor from the real object. The ONLY provably-safe resume is therefore the IMMEDIATE retry: the SAME session
 * (record.sessionId) with the session revision STILL EXACTLY the rename's (record.revision) — i.e. NOTHING has mutated
 * since the rename committed. The caller then CAS-fences the write on that revision so any interleaving mutation is
 * refused atomically inside the engine's single-writer dispatch. This replaced the old liveness-probe resume (a probe
 * cannot vouch for identity), so this function no longer takes ref-liveness inputs — it compares the live session +
 * revision the caller sampled at Save time against the record.
 *
 * ANY drift refuses. A drift we can act on with certainty (same session, but the revision MOVED -> something mutated,
 * possibly an impostor) TOMBSTONES the record: keep it `state:'dead'` so this and every future save from the editor is a
 * permanent, fail-closed refusal (revision is monotonic, so it can never return to the rename's value — the refusal is
 * inherently permanent, and a persisted tombstone makes it explicit). A drift we CANNOT act on with certainty (a
 * DIFFERENT session -- a reload/reopen, whose record can never match a future session either) rejects WITHOUT mutating
 * the record (HIGH 2: never change a record's state off a cross-session comparison); a clean close / sweep clears it.
 * The DAX is never lost either way — the editor stays open + dirty; the user reopens from the tree and re-applies it.
 *   no record                                  -> proceed(oldRef)     (normal flow; the old ref is still the object)
 *   record, wrong model                        -> reject              (never resume a rename into a DIFFERENT model; no tombstone)
 *   record already TOMBSTONE (state:'dead')    -> reject + tombstone  (keep the dead marker; permanent fail-closed refusal)
 *   live session/revision unconfirmed          -> reject              (transient; retry when connected; no state change)
 *   record, DIFFERENT session (reload/reopen)  -> reject              (can't vouch for identity across sessions; no state change, HIGH 2)
 *   record, SAME session, revision MOVED       -> reject + tombstone  (something mutated -> possible impostor; permanent refusal)
 *   record, SAME session, revision UNCHANGED   -> proceed(newRef)     (the one safe resume: immediate retry, CAS-fenced by the caller)
 */
function decideRenameRecovery(record, oldRef, liveModelKey, liveSessionId, liveRevision) {
    if (!record)
        return { kind: 'proceed', baseRef: oldRef };
    if (record.modelKey !== liveModelKey) {
        // A record from ANOTHER model session: never resume it here, and never tombstone it (it may be valid in the model
        // it belongs to).
        return { kind: 'reject', reason: 'This unsaved rename was made in a different model session. Reopen the object from the current model, then Save.' };
    }
    // A TOMBSTONE is a permanent, fail-closed refusal; keep it (tombstone:true) so it survives until a clean close / sweep.
    if (record.state === 'dead') {
        return { kind: 'reject', tombstone: true, reason: TOMBSTONE_REASON };
    }
    // Fail SAFE on an unconfirmed live session/revision (a transient read failure): don't act, and don't mutate the record.
    if (liveSessionId === undefined || liveRevision === undefined) {
        return { kind: 'reject', reason: "Couldn't confirm the live model session, so this unsaved rename can't be resumed yet. Try again when connected." };
    }
    // A DIFFERENT (or unrecorded) session -- a reload/reopen. The immediate-retry chain is broken and a probe can't vouch
    // for the object's identity across sessions, so we refuse. HIGH 2: do NOT change the record's state off a cross-session
    // comparison; leave it for a clean close / sweep. (The record's session can never match a future one, so this is
    // permanent anyway, without a persisted tombstone.)
    if (record.sessionId === undefined || record.revision === undefined || record.sessionId !== liveSessionId) {
        return { kind: 'reject', reason: RESUME_UNSAFE_REASON };
    }
    // SAME session, but the revision MOVED: something mutated since the rename committed -- possibly a delete+recreate of an
    // impostor at the new name, which no probe can distinguish. This can NEVER be safely resumed, so TOMBSTONE it.
    if (record.revision !== liveRevision) {
        return { kind: 'reject', tombstone: true, reason: TOMBSTONE_REASON };
    }
    // SAME session + revision UNCHANGED since the rename: nothing has mutated, so the object still lives at newRef exactly
    // as renamed. The one safe resume; the caller CAS-fences the write on record.revision.
    return { kind: 'proceed', baseRef: record.newRef };
}
// One honest message for a rename that can no longer be safely resumed here (a tombstone, or a cross-session reload). The
// object may still exist, but with name-based refs we can't prove it's the one we renamed, so we ask the user to reopen
// from the tree and re-apply their (preserved) DAX. NB no em/en dashes — product copy rule.
const TOMBSTONE_REASON = 'This unsaved rename can no longer be safely resumed here because the model changed after it was interrupted. Reopen the object from the Model tree, then re-apply your DAX.';
const RESUME_UNSAFE_REASON = 'This unsaved rename was interrupted in an earlier session, so it can no longer be safely resumed here. Reopen the object from the Model tree, then re-apply your DAX.';
/**
 * A stable, url-safe token for a model-identity string (its source path, else the session id), stamped onto a header
 * uri's query so the OWNING model travels WITH the uri (surviving a window reload) and a cross-model dirty buffer is
 * detectable at Save. CRITICAL 2: this token GATES a cross-model rename/write, so it must be collision-RESISTANT -- a
 * findable collision (the old 32-bit FNV had only ~2^16 candidates) would let an editor for model A rename or overwrite
 * model B's same-named object. So we carry the CANONICAL identity itself, base64url-encoded (reversible, so distinct keys
 * always yield distinct tokens -- no collision to find), and fall back to a 128-bit SHA-256 prefix (2^128 space) only for
 * a pathologically long key. Both alphabets ([A-Za-z0-9_-] / hex) pass through a uri query and URLSearchParams UNCHANGED;
 * percent-escapes would be altered by a decode round-trip and break the compare.
 */
function identityToken(key) {
    const enc = node_buffer_1.Buffer.from(key, 'utf8').toString('base64url'); // reversible, url-safe, decode-invariant
    if (enc.length <= 512)
        return enc; // carry the exact identity: no collision possible
    return (0, node_crypto_1.createHash)('sha256').update(key, 'utf8').digest('hex').slice(0, 32); // 128-bit prefix for over-long keys
}
/** Split a header-doc into its first line (header) and the DAX body below it. */
function splitDaxHeader(text) {
    const nl = text.indexOf('\n');
    if (nl < 0)
        return { header: text, body: '', headerLen: text.length };
    return { header: text.slice(0, nl), body: text.slice(nl + 1), headerLen: nl + 1 };
}
/**
 * Re-key a ref after a rename by stripping the KNOWN old-name suffix and appending the new name. '/' is BOTH the ref
 * separator AND a legal name char, so we never splice at a slash -- the name (slashes and all) is always the literal
 * tail of the ref (same technique the Properties grid uses). Returns the old ref unchanged if it doesn't end with the
 * old name (should never happen) rather than guess and corrupt.
 */
function reKeyRef(ref, oldName, newName) {
    return (oldName && ref.endsWith(oldName)) ? ref.slice(0, ref.length - oldName.length) + newName : ref;
}
/** First free "<base>" / "<base> 2" / "<base> 3"... not already taken (case-insensitive). `sep` is ' ' for names
 *  with spaces, '' for UDFs (which forbid spaces). */
function uniqueName(base, existingLower, sep = ' ') {
    if (!existingLower.has(base.toLowerCase()))
        return base;
    for (let i = 2;; i++) {
        const candidate = `${base}${sep}${i}`;
        if (!existingLower.has(candidate.toLowerCase()))
            return candidate;
    }
}
//# sourceMappingURL=daxHeader.js.map