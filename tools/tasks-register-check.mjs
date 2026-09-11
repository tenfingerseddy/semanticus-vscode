#!/usr/bin/env node
// TASKS.md is the canonical status register, and its task ids are its primary keys. Two agents working in
// parallel both read origin/main, both took the next free ids, and both took the SAME ones, so two pull
// requests defined T177-T181 meaning ten different things. One merged; the other only surfaced as a merge
// conflict. Nothing in the repo would have caught it.
//
//   node tools/tasks-register-check.mjs [path-to-register]
//
// Exit 1 when any id has more than one ALLOCATION. A definition row is still the canonical bullet that
// allocates an id. Duplicate allocations also include a real top-level heading whose first rendered item is
// the same id. Definition-row counts stay unordered-list bullets only.
//
// A DEFINITION row opens with the id:            - [T177] Do the thing ...
// A REFERENCE row opens with a DATE:             - 2026-07-22 [T161] Kane ratified ...
//
// That distinction is the whole correctness of this check. The append-only DONE log at the bottom of the
// register cites ids that were defined earlier and then removed from the live list when they shipped, so
// treating a citation as a definition would report the entire log as duplicates, and treating a definition
// as a citation would report nothing at all.
//
// Ids cited without any definition row are recorded but never fail the run: a shipped task's
// definition row is deliberately gone, so that set is large by design.
// T206: keep the work behind each id in the report, and refuse identical work under different ids.
// Only whitespace and id decoration are normalised. This is not a semantic paraphrase detector.

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// An UNORDERED LIST row is where this register allocates ids. Both readings used to require `-` followed by a
// space, so a duplicate written `* [T140]`, `+ [T140]`, `-[T140]` or `- *[T140]*` was invisible.
const ROW_OPENER = String.raw`[-*+][ \t]*`;

// ORDERED LISTS AND TABLE CELLS ARE CITATION SITES HERE, NOT DEFINITIONS, and that is measured rather than
// assumed. On the register at 14269af9 the unordered reading found exactly 39 rows and 39 distinct ids while
// 21 rows put an id in definition position behind a heading, a numbered item or a table cell: the
// `## [T165] DAX KERNEL PROGRAMME` heading, and twenty numbered "what needs Kane" items citing T164, T160,
// T109 and so on. Counting every one of those as a definition row turns the register into 59 rows, 40 ids and
// 18 DUPLICATES, all false, and fails the gate on prose that is doing nothing wrong.
//
// Twenty of the 21 were citations. ONE was not: T165 had no definition row anywhere, so the heading was its
// sole allocation site and it was the one live id this check could not protect. The unprotected result still
// catches a heading that is the only leading appearance of an id, and the canonical `- [T165]` row is still
// the definition the rest of this file reads. Headings are the remaining hole that those two do not close: a
// heading that ALSO has a unique bullet is invisible to both, because the heading is not a definition row and
// the bullet is not a duplicate. Real top-level headings whose first rendered item is an id therefore join
// the duplicate set without joining the definition-row count. Ordered lists and table cells stay citations.
// Date-first headings and ids after visible prose stay citations, which is why the current T165 heading can
// keep its id after the title. Fenced, indented, blockquoted and list-nested headings are not allocations.
// List-container state survives nested fences and quotes, and the content column follows CommonMark marker
// indentation. A fenced or raw HTML block opened inside a list item ends WITH that item, so a dedented
// sibling row and everything after it is read rather than swallowed. Raw HTML blocks and HTML comments are
// not headings. An escaped leading id is still an id. Decoded Unicode whitespace and zero-width decoration
// count as leading decoration, and character references stay LITERAL inside a code span, so an encoded
// bracket citation written there is a citation and not an allocation.
// Unsupported leading HTML or entity decoration fails loudly only when it hides an id at allocation
// position; a no-id or prose-first HTML/entity heading is a citation. The test pins that split so it stays
// a decision.
//
// Rebased onto main at bfd556ad the register reads 47 rows / 47 ids / 0 duplicates / 128 orphans; on this
// branch alone it read 40/40/0/128, and at 14269af9 it read 39/39/0/129. Every one of those moves was DATA,
// not this parser: main itself went 39 to 46 (three rows in #290, four in #285, measured commit by commit),
// and the T165 row above adds the 47th while closing one orphan. Row counts are therefore deliberately NOT
// asserted anywhere; only the invariants are (zero duplicates, zero unrecognised, zero unprotected), because
// a pinned count would fail on every honest register edit.
//
// The residual risk that remains is the ordered-list and table form: if someone allocates an id as
// `1. [T200] new task` AND also cites it in an unordered row, this still will not see the allocation. A
// heading-form allocation of that same shape now fails.
const CITATION_OPENER = String.raw`(?:\d{1,9}[.)][ \t]*|\|[ \t]*)`;

// A definition row: a row opener, then optional markdown decoration and an optional task-list checkbox, then
// the id. The id must come before any letter, which is what makes it a definition rather than a citation.
const DEFINITION = new RegExp(String.raw`^[ \t]*${ROW_OPENER}(?:\[[ xX]\][ \t]*)?[*_\`~]{0,2}\[T(\d+)\]`);

// A DELIBERATELY CRUDE second reading, used only to catch the first one going blind. Any row that puts an id
// ahead of every letter is in definition position, whatever decorates it. A row this sees and DEFINITION does
// not is either a definition the check cannot read or a format change, and both mean the check is only partly
// reading the file. Measured on the register at 14269af9 the two agree exactly, so this guards a latent hole
// rather than papering over a live one.
const DEFINITION_POSITION = new RegExp(String.raw`^[ \t]*${ROW_OPENER}[^A-Za-z\n]{0,12}\[T(\d+)\]`);

// The same leading position, but behind a numbered item or a table cell. These are not treated as definitions,
// for the reasons above, but an id that appears ONLY here has been allocated in a shape the gate cannot read,
// and is therefore the one id the duplicate check cannot protect.
//
// THE FIX FOR THAT SHAPE IS THE DATA, NOT THIS PARSER. A gate is allowed to require a canonical shape, and
// requiring one is better than teaching the gate every shape a human might invent. So this reports, and a
// human adds the canonical row. Headings used to live in this bucket; they still report as unprotected when
// they are the only leading appearance, but they also join the duplicate set, because a heading plus a unique
// bullet was the hole F-018 left open.
const CITED_POSITION = new RegExp(String.raw`^[ \t]*${CITATION_OPENER}[^A-Za-z\n]{0,12}\[T(\d+)\]`);
// A dated row is a DONE-log citation, never a definition. Checked first, because a citation can otherwise
// satisfy DEFINITION_POSITION and the whole append-only log would read as unrecognised definitions.
const DATED_CITATION = new RegExp(String.raw`^[ \t]*${ROW_OPENER}\d{4}-\d{2}-\d{2}\b`);

// Deliberately BARE rather than bracketed. Only the definition rule decides pass or fail; this one feeds the
// reported reference count, and prose cites ids unbracketed ("renumbered T177 to T181"), so the bracketed
// form undercounts. Measured on the register at 14269af9: bracketed gives 77 ids without a definition row,
// bare gives 129, and 129 is the agreed calibration figure. It is looser and will occasionally count a token
// that merely looks like an id, which is affordable precisely because it can never fail the run.
const REFERENCE = /\bT(\d+)\b/g;

const ATX_HEADING = /^( {0,3})(#{1,6})(?:[ \t]+|$)(.*)$/;
const SETEXT_UNDERLINE = /^( {0,3})(?:=+|-+)[ \t]*$/;
const FENCE_OPEN = /^( {0,3})(`{3,}|~{3,})(.*)$/;
const BLOCKQUOTE = /^( {0,3})>/;
const THEMATIC_BREAK = /^( {0,3})(?:(?:-[ \t]*){3,}|(?:\*[ \t]*){3,}|(?:_[ \t]*){3,})$/;
const LINK_DEFINITION = /^ {0,3}\[[^\]]+\]:[ \t]*(?:\S.*)?$/;
const HTML_BLANK_TAGS = 'address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul';
const HTML_TYPE6 = new RegExp(String.raw`^ {0,3}</?(?:${HTML_BLANK_TAGS})(?:\s|/?>|$)`, 'i');
const HTML_TYPE1 = /^ {0,3}<\/?(?:pre|script|style|textarea)(?:\s|>|$)/i;
// This is the entity-decoding root for every character class the allocation grammar consumes. Keep names
// case-sensitive: CommonMark resolves &NewLine; and &Tab;, but &NBSP; is not the HTML &nbsp; entity.
const NAMED_ENTITIES = new Map([
// Named aliases for every supported whitespace/format character, plus the bracket grammar.
// ThickSpace decodes to two whitespace characters; classification must inspect both.
    ["MediumSpace", 8287],
    ["NegativeMediumSpace", 8203],
    ["NegativeThickSpace", 8203],
    ["NegativeThinSpace", 8203],
    ["NegativeVeryThinSpace", 8203],
    ["NewLine", 10],
    ["NonBreakingSpace", 160],
    ["Tab", 9],
    ["ThickSpace", [8287, 8202]],
    ["ThinSpace", 8201],
    ["VeryThinSpace", 8202],
    ["ZeroWidthSpace", 8203],
    ["emsp", 8195],
    ["emsp13", 8196],
    ["emsp14", 8197],
    ["ensp", 8194],
    ["hairsp", 8202],
    ["lbrack", 91],
    ["lrm", 8206],
    ["nbsp", 160],
    ["numsp", 8199],
    ["puncsp", 8200],
    ["rbrack", 93],
    ["rlm", 8207],
    ["thinsp", 8201],
    ["zwj", 8205],
    ["zwnj", 8204],
]);

// CommonMark's Unicode whitespace set is ASCII whitespace plus the Unicode space separators (Zs). The
// leading-markup loop below used to accept `cp <= 32 || cp === 160` and nothing else, so `&ensp;`, `&emsp;`
// and `&thinsp;` decoded to characters it did not recognise as space: the loop stopped, the id behind them
// was never reached, and the heading was filed as an ordinary citation. Silent, and free to duplicate a
// canonical row unseen. That was F-111, and the character class was the defect, not the three examples.
const UNICODE_SPACE_SEPARATOR = new Set([
    0x20, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009,
    0x200a, 0x202f, 0x205f, 0x3000,
]);

// NOT whitespace, and deliberately kept apart from the set above so the distinction stays readable. These are
// invisible for a different reason (zero width, or a bidi mark), they were already in the entity root with
// nothing consuming them, and they hide a leading id in exactly the same way. Treating them as decoration
// costs nothing: the only thing that changes is that a heading hiding an id behind one now fails loudly
// instead of passing as a citation, and loud is the direction this file has chosen everywhere else.
const INVISIBLE_FORMAT = new Set([0x200b, 0x200c, 0x200d, 0x200e, 0x200f, 0xfeff]);

// `cp <= 32` is kept rather than narrowed to the seven real ASCII whitespace codepoints: widening what counts
// as decoration can only turn a silent citation into a loud report, and narrowing it does the reverse.
function isInvisibleLead(cp) {
    return cp <= 32 || UNICODE_SPACE_SEPARATOR.has(cp) || INVISIBLE_FORMAT.has(cp);
}

function unwrapOnce(s) {
    s = s.replace(/^[ \t]+/, '');
    const pairs = [['**', '**'], ['__', '__'], ['~~', '~~'], ['*', '*'], ['_', '_']];
    for (const [open, close] of pairs) {
        if (open === '*' && s.startsWith('**')) continue;
        if (open === '_' && s.startsWith('__')) continue;
        if (!s.startsWith(open)) continue;
        const closeAt = s.indexOf(close, open.length);
        if (closeAt === -1) continue;
        return s.slice(open.length, closeAt) + s.slice(closeAt + close.length);
    }
    const wrappedId = /^\[(\[T\d+\])\](?:\([^)]*\)|\[[^\]]*\])/.exec(s);
    if (wrappedId) return wrappedId[1] + s.slice(wrappedId[0].length);
    const idLink = /^(\[T\d+\])(?:\([^)]+\)|\[[^\]]+\])/.exec(s);
    if (idLink) return idLink[1] + s.slice(idLink[0].length);
    return null;
}

function leadingEntity(s) {
    const m = /^&(?:#[xX]([0-9a-fA-F]+)|#(\d+)|([a-zA-Z][a-zA-Z0-9]*));/.exec(s);
    if (!m) return null;
    let cp;
    if (m[1] !== undefined) cp = Number.parseInt(m[1], 16);
    else if (m[2] !== undefined) cp = Number.parseInt(m[2], 10);
    else cp = NAMED_ENTITIES.get(m[3]);
    const cps = Array.isArray(cp) ? cp : [cp];
    if (cps.some(value => !Number.isSafeInteger(value) || value < 0 || value > 0x10ffff)) return null;
    return { raw: m[0], cp: cps.length === 1 ? cps[0] : null, cps };
}

// Reads one complete inline tag without mistaking a > inside a quoted attribute for its end.
function inlineTagLength(s) {
    if (!/^<\/?[a-zA-Z]/.test(s)) return 0;
    let quote = null;
    for (let i = 1; i < s.length; i++) {
        const ch = s[i];
        if (quote !== null) {
            if (ch === quote) quote = null;
        } else if (ch === '"' || ch === "'") {
            quote = ch;
        } else if (ch === '>') {
            return i + 1;
        }
    }
    return 0;
}

function stripLeadingMarkup(s) {
    for (;;) {
        s = s.replace(/^[ \t]+/, '');
        if (s.startsWith('<!--')) {
            const end = s.indexOf('-->', 4);
            if (end === -1) return s;
            s = s.slice(end + 3);
            continue;
        }
        if (s.startsWith('<')) {
            const length = inlineTagLength(s);
            if (length === 0) return s;
            s = s.slice(length);
            continue;
        }
        if (s.startsWith('&')) {
            const ent = leadingEntity(s);
            if (!ent || !ent.cps.every(isInvisibleLead)) return s;
            s = s.slice(ent.raw.length);
            continue;
        }
        return s;
    }
}

// `literal` says the text came out of a CODE SPAN, where inline parsing does not happen at all: backslash
// escapes and character references are both the characters they are written as. One flag governs both because
// it is one rule, and splitting it was the bug. The escape half was already honoured; the entity half was not,
// so `` `&#91;T140&#93;` `` decoded to `[T140]` and a citation was counted as an allocation, colliding with the
// canonical row. That was F-112, and its cost is the expensive direction: a FALSE gate failure on prose that
// is doing nothing wrong.
//
// Outside a code span both resolve before inline parsing, so an id may legitimately arrive escaped or with
// either bracket encoded, and both readings still find it.
function taskIdLead(s, literal) {
    s = s.replace(/^[ \t]+/, '');
    if (!literal) {
        if (/^\\\[T\d+\]/.test(s)) s = s.slice(1);

        // Decode bracket references across the small leading token, not only its first character, so both
        // brackets may be encoded.
        s = s.replace(/&(?:#[xX][0-9a-fA-F]+|#\d+|[a-zA-Z][a-zA-Z0-9]*);/g, raw => {
            const ent = leadingEntity(raw);
            return ent && (ent.cp === 91 || ent.cp === 93) ? String.fromCodePoint(ent.cp) : raw;
        });
    }
    const id = /^\[T(\d+)\]/.exec(s);
    return id ? Number(id[1]) : null;
}

function headingLead(text) {
    let s = text.replace(/[ \t]+#+[ \t]*$/, '');
    s = s.replace(/^[ \t]+/, '');

    // CommonMark backslash escapes are literal inside code spans. Closing backticks must be a run exactly as
    // long as the opener; a shorter run inside a double-backtick span is content, not its closing delimiter.
    if (s.startsWith('`')) {
        const openLength = /^`+/.exec(s)[0].length;
        let at = openLength;
        while (at < s.length) {
            const next = s.indexOf('`', at);
            if (next === -1) break;
            const runLength = /^`+/.exec(s.slice(next))[0].length;
            if (runLength === openLength) {
                const id = taskIdLead(s.slice(openLength, next), true);
                return id === null ? { kind: 'citation' } : { kind: 'allocation', id };
            }
            at = next + runLength;
        }
    }

    for (;;) {
        const next = unwrapOnce(s);
        if (next === null) break;
        s = next;
    }
    s = s.replace(/^[ \t]+/, '');

    const direct = taskIdLead(s, false);
    if (direct !== null) return { kind: 'allocation', id: direct };

    if (s.startsWith('<') || s.startsWith('&')) {
        const stripped = stripLeadingMarkup(s);
        if (taskIdLead(stripped, false) !== null) return { kind: 'unsupported' };
    }
    return { kind: 'citation' };
}

function fenceOpen(line) {
    const m = FENCE_OPEN.exec(line);
    if (!m) return null;
    const marker = m[2];
    const info = m[3] ?? '';
    if (marker[0] === '`' && info.includes('`')) return null;
    return { char: marker[0], len: marker.length, indent: m[1].length };
}

function fenceClose(line, open) {
    const m = /^( {0,3})(`{3,}|~{3,})[ \t]*$/.exec(line);
    if (!m) return false;
    if (m[2][0] !== open.char) return false;
    return m[2].length >= open.len;
}

function indentation(line) {
    let chars = 0;
    let columns = 0;
    while (chars < line.length) {
        if (line[chars] === ' ') columns++;
        else if (line[chars] === '\t') columns += 4 - (columns % 4);
        else break;
        chars++;
    }
    return { chars, columns };
}

function isType7HtmlTag(line) {
    const lead = /^( {0,3})</.exec(line);
    if (!lead) return false;
    const start = lead[1].length;
    const length = inlineTagLength(line.slice(start));
    return length > 0 && line.slice(start + length).trim() === '';
}

function htmlBlockOpen(line) {
    if (!/^ {0,3}</.test(line)) return null;
    const indent = indentation(line).columns;
    if (/^ {0,3}<!--/.test(line)) return { indent, until: /-->/ };
    if (/^ {0,3}<\?/.test(line)) return { indent, until: /\?>/ };
    if (/^ {0,3}<!\[CDATA\[/i.test(line)) return { indent, until: /\]\]>/ };
    if (/^ {0,3}<![a-zA-Z]/.test(line)) return { indent, until: />/ };
    if (HTML_TYPE1.test(line)) return { indent, until: /<\/(?:pre|script|style|textarea)>/i };
    if (HTML_TYPE6.test(line)) return { indent, untilBlank: true };
    if (isType7HtmlTag(line)) return { indent, untilBlank: true, cannotInterruptParagraph: true };
    return null;
}

// A block inside a list sees indentation after the owning item's content columns.
// Expand only the structural prefix so a tab crossing that boundary keeps its remaining spaces.
function relativeBlockLine(line, container) {
    if (container === null) return line;
    const { chars, columns } = indentation(line);
    if (columns < container) return line;
    return ' '.repeat(columns - container) + line.slice(chars);
}

function nestedInList(indent, listContentCol) {
    return listContentCol !== null && indent >= listContentCol && indent > 0;
}

// CommonMark 5.2: an ordered marker has one through nine digits and ends in . or ). Marker indent is 0-3
// columns. Padding is measured in columns, not characters, because a tab advances to the next four-column
// stop. Five or more columns before nonblank content count as one pad plus content indent.
function listContentColumn(line, paragraphOpen = false) {
    if (THEMATIC_BREAK.test(line)) return null;
    const lead = /^( {0,3})([-*+]|\d{1,9}[.)])/.exec(line);
    if (!lead) return null;
    if (paragraphOpen && /^\d/.test(lead[2]) && Number.parseInt(lead[2], 10) !== 1) return null;
    const markerIndent = lead[1].length;
    const markerEnd = lead[0].length;
    const markerEndColumn = markerIndent + lead[2].length;
    if (markerEnd === line.length) return markerEndColumn + 1;
    if (line[markerEnd] !== ' ' && line[markerEnd] !== '\t') return null;

    let at = markerEnd;
    let column = markerEndColumn;
    while (at < line.length && (line[at] === ' ' || line[at] === '\t')) {
        if (line[at] === ' ') column++;
        else column += 4 - (column % 4);
        at++;
    }
    const padding = column - markerEndColumn;
    if (at === line.length) return markerEndColumn + 1;
    return padding <= 4 ? column : markerEndColumn + 1;
}

function isSetextContentLine(line, paragraphOpen = false) {
    if (line.trim() === '') return false;
    if (SETEXT_UNDERLINE.test(line)) return false;
    if (THEMATIC_BREAK.test(line)) return false;
    if (ATX_HEADING.test(line)) return false;
    if (fenceOpen(line)) return false;
    if (BLOCKQUOTE.test(line)) return false;
    if (indentation(line).columns >= 4) return false;
    const html = htmlBlockOpen(line);
    if (html && !(paragraphOpen && html.cannotInterruptParagraph)) return false;
    if (listContentColumn(line, paragraphOpen) !== null) return false;
    if (LINK_DEFINITION.test(line)) return false;
    return true;
}

function recordHeading(result, lineText, at, allocations, unsupported) {
    if (result.kind === 'allocation') allocations.push({ id: result.id, line: at });
    else if (result.kind === 'unsupported') {
        unsupported.push({ line: at, text: lineText.trim().slice(0, 120) });
    }
}

// Top-level headings only. Bullet rows are parsed separately, but consume this scan's inert-line state so a
// bullet-shaped line inside the same HTML or fenced block cannot become a canonical definition.
function scanHeadingAllocations(lines) {
    const allocations = [];
    const unsupported = [];
    const inert = new Set();
    let fence = null;
    let html = null;
    // The content column of the list item that CONTAINS the open fenced or HTML block, or null when the open
    // block is top-level. See the containment rule at the head of the loop.
    let inertContainerCol = null;
    let listContentCol = null;
    let paragraphOpen = false;

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        const at = i + 1;

        // A fenced or raw HTML block opened inside a list item is INSIDE that item, so the item ending ends
        // the block. A fenced block gets no lazy continuation, so the first non-blank line left of the item's
        // content column - a dedented sibling item, a heading, any top-level row - closes the item and the
        // block with it. Nothing did that, so an unclosed nested block stayed inert to the end of the file and
        // the sibling and EVERY later task row went unread. That was F-110, and its shape is the worst one
        // this file has: with rows already counted above the list the blindness guard never fires, so a
        // genuine duplicate below it is reported as a clean register.
        //
        // Blank lines are excluded: a blank line does not end a list item, and it is ordinary content inside
        // a fence. The container recorded here is the OUTERMOST open item, which is the only one this scan
        // tracks, so a block opened inside a nested sub-list stays inert until a line dedents left of the
        // outer item. That residual is deliberate and it is the safe direction: this rule can only fail to
        // read a row, never invent an allocation out of code or raw HTML, which is the error that fails the
        // gate on prose that is doing nothing wrong.
        if ((html || fence) && inertContainerCol !== null && line.trim() !== ''
            && indentation(line).columns < inertContainerCol) {
            html = null;
            fence = null;
            inertContainerCol = null;
        }

        if (html) {
            inert.add(i);
            if (html.until) {
                if (html.until.test(line)) {
                    html = null;
                    inertContainerCol = null;
                }
            } else if (line.trim() === '') {
                html = null;
                inertContainerCol = null;
            }
            continue;
        }

        if (fence) {
            inert.add(i);
            if (fenceClose(relativeBlockLine(line, inertContainerCol), fence)) {
                fence = null;
                inertContainerCol = null;
            }
            continue;
        }

        const container = nestedInList(indentation(line).columns, listContentCol) ? listContentCol : null;
        const blockLine = relativeBlockLine(line, container);
        const openedHtml = htmlBlockOpen(blockLine);
        if (openedHtml) openedHtml.indent += container ?? 0;
        if (openedHtml && !(paragraphOpen && openedHtml.cannotInterruptParagraph)) {
            inert.add(i);
            paragraphOpen = false;
            const nestedHtml = nestedInList(openedHtml.indent, listContentCol);
            if (!nestedHtml) listContentCol = null;
            if (openedHtml.until && openedHtml.until.test(line)) {
                // opened and closed on the same line
            } else {
                html = openedHtml;
                inertContainerCol = nestedHtml ? listContentCol : null;
            }
            continue;
        }

        const opened = fenceOpen(blockLine);
        if (opened) opened.indent += container ?? 0;
        if (opened) {
            inert.add(i);
            paragraphOpen = false;
            const nestedFence = nestedInList(opened.indent, listContentCol);
            if (!nestedFence) listContentCol = null;
            fence = opened;
            inertContainerCol = nestedFence ? listContentCol : null;
            continue;
        }

        if (BLOCKQUOTE.test(line)) {
            paragraphOpen = false;
            if (!nestedInList(indentation(line).columns, listContentCol)) listContentCol = null;
            continue;
        }

        if (indentation(line).columns >= 4) continue;

        if (line.trim() === '') {
            paragraphOpen = false;
            continue;
        }

        if (THEMATIC_BREAK.test(line)) {
            paragraphOpen = false;
            continue;
        }

        const indent = indentation(line).columns;
        const nested = nestedInList(indent, listContentCol);

        const itemCol = listContentColumn(line, paragraphOpen);
        if (itemCol !== null && !nested) {
            paragraphOpen = false;
            listContentCol = itemCol;
            continue;
        }

        if (nested) continue;

        listContentCol = null;

        const atx = ATX_HEADING.exec(line);
        if (atx) {
            paragraphOpen = false;
            recordHeading(headingLead(atx[3] ?? ''), line, at, allocations, unsupported);
            continue;
        }

        if (isSetextContentLine(line, paragraphOpen)) {
            let j = i + 1;
            while (j < lines.length && isSetextContentLine(lines[j], true)) j++;
            if (j < lines.length && SETEXT_UNDERLINE.test(lines[j])) {
                const content = lines.slice(i, j).map(l => l.replace(/^[ \t]+/, '')).join(' ');
                paragraphOpen = false;
                recordHeading(headingLead(content), line, at, allocations, unsupported);
                i = j;
                continue;
            }
            paragraphOpen = true;
        }
    }

    return { allocations, unsupported, inert };
}

function record(map, id, line) {
    if (!map.has(id)) map.set(id, []);
    map.get(id).push(line);
}

export function analyse(text) {
    const lines = text.split(/\r?\n/);
    const headings = scanHeadingAllocations(lines);

    const definitions = new Map();   // id -> [line numbers], canonical bullet rows only
    const allocations = new Map();   // id -> [line numbers], bullets plus heading-form leading ids
    const referenced = new Set();
    const tasks = [];
    const unrecognised = [];
    const inCitationPosition = new Map();   // id -> [line numbers], heading / ordered list / table

    lines.forEach((line, index) => {
        const at = index + 1;
        const definition = headings.inert.has(index) ? null : DEFINITION.exec(line);
        if (definition) {
            const id = Number(definition[1]);
            record(definitions, id, at);
            record(allocations, id, at);
            const body = [line.slice(definition[0].length).replace(/^[*_`~]{0,2}/u, '')];
            // A task includes its indented continuation, not just the opening sentence.
            // Stop at the next allocation or top-level prose, not at a wrapped line.
            for (let i = index + 1; i < lines.length; i++) {
                if (DEFINITION.test(lines[i]) || (lines[i].trim() && !/^[ \t]/u.test(lines[i]))) break;
                body.push(lines[i]);
            }
            tasks.push({ id, line: at, work: body.join(' ').trim().replace(/\s+/gu, ' ') });
        } else if (!headings.inert.has(index) && !DATED_CITATION.test(line) && DEFINITION_POSITION.test(line)) {
            unrecognised.push({ line: at, text: line.trim().slice(0, 120) });
        } else if (!headings.inert.has(index) && !DATED_CITATION.test(line)) {
            // An id leading an ordered-list item or a table cell. Normally a citation, but it is the shape
            // an ALLOCATION hides in, so it is recorded and cross-checked below.
            const cited = CITED_POSITION.exec(line);
            if (cited) {
                const id = Number(cited[1]);
                record(inCitationPosition, id, at);
            }
        }

        for (const match of line.matchAll(REFERENCE)) referenced.add(Number(match[1]));
    });

    for (const { id, line } of headings.allocations) {
        record(inCitationPosition, id, line);
        record(allocations, id, line);
    }
    for (const row of headings.unsupported) unrecognised.push(row);
    for (const at of allocations.values()) at.sort((a, b) => a - b);
    for (const at of inCitationPosition.values()) at.sort((a, b) => a - b);
    unrecognised.sort((a, b) => a.line - b.line);

    const rows = [...definitions.values()].reduce((n, at) => n + at.length, 0);
    const duplicates = [...allocations.entries()].filter(([, at]) => at.length > 1).sort((a, b) => a[0] - b[0]);
    const orphans = [...referenced].filter(id => !definitions.has(id)).sort((a, b) => a - b);

    // An id whose ONLY leading appearance is behind a citation opener was allocated where the gate cannot see
    // it. This is the general form of the T165 bug, and it catches it without anyone counting rows by hand.
    const unprotected = [...inCitationPosition.entries()]
        .filter(([id]) => !definitions.has(id))
        .map(([id, at]) => ({ id, at }))
        .sort((a, b) => a.id - b.id);

    const byWork = new Map();
    for (const task of tasks) {
        if (!task.work) continue;
        if (!byWork.has(task.work)) byWork.set(task.work, []);
        byWork.get(task.work).push(task);
    }
    const duplicateWork = [...byWork.values()].filter(group => new Set(group.map(task => task.id)).size > 1);
    tasks.sort((a, b) => a.id - b.id || a.line - b.line);
    return { rows, definitions, allocations, tasks, duplicateWork, duplicates, orphans, unrecognised, unprotected };
}

export function main(path, log = console.log, warn = console.error) {
    const { rows, definitions, allocations, tasks, duplicateWork, duplicates, orphans, unrecognised, unprotected } = analyse(readFileSync(path, 'utf8'));

    log(`${path}`);
    log(`  definition rows:          ${rows}`);
    log(`  distinct ids defined:     ${definitions.size}`);
    log(`  duplicate allocations:    ${duplicates.length}`);
    log(`  referenced, not defined:  ${orphans.length}   (shipped tasks keep their DONE-log citation)`);
    // Counts remain useful headings, but only the collections distinguish equal-sized changes.
    log(`  task collection: ${JSON.stringify(tasks)}`);
    log(`  allocation collection: ${JSON.stringify([...allocations].sort((a, b) => a[0] - b[0]))}`);
    log(`  orphan collection: ${JSON.stringify(orphans)}`);

    if (rows === 0) {
        warn('\nNo definition rows found at all. The register format changed and this check is now blind,');
        warn('which is worse than a duplicate id. Fix the pattern, do not delete the check.');
        return 1;
    }

    if (unrecognised.length > 0) {
        warn(`\nRows that put an id in allocation position but that no rule matched:`);
        for (const row of unrecognised) warn(`  line ${row.line}: ${row.text}`);
        warn('\nEither these are allocations the check is blind to, or the register grew a row shape this');
        warn('check does not know, including unsupported leading HTML or entity decoration on a heading.');
        warn('Both mean it is only partly reading the file, so it fails rather than reporting a clean run');
        warn('over the part it happens to understand.');
        return 1;
    }

    if (unprotected.length > 0) {
        warn('\nIds allocated where this check cannot read them, so the duplicate rule cannot protect them:');
        for (const { id, at } of unprotected)
            warn(`  T${id} leads a heading, an ordered-list item or a table cell (line ${at.join(', ')}) and has no definition row`);
        warn('\nAdd a plain `- [T###] ...` row for each. If the id leads a heading, take it out of heading-leading');
        warn('position as well: a heading that still leads with the same id is a second allocation, and the');
        warn('duplicate rule will then refuse it. Ordered-list and table citations may keep their form. Fixing');
        warn('the register rather than widening the definition rule is deliberate: a gate may require a canonical');
        warn('shape, and requiring one beats teaching it every shape a human might invent.');
        return 1;
    }

    if (duplicates.length > 0) {
        warn(`\nDuplicate task id allocations in ${path}:`);
        for (const [id, at] of duplicates)
            warn(`  T${id} is allocated ${at.length} times, on lines ${at.join(', ')}`);
        warn('\nTwo allocations of one id means two pieces of work share a primary key. Renumber the newer');
        warn('one to the next free id, and take the ids from the MERGED register rather than the branch');
        warn('point, because that is how the collision happens.');
        return 1;
    }

    if (duplicateWork.length > 0) {
        warn(`\nDifferent task ids allocate the same work in ${path}:`);
        for (const group of duplicateWork) {
            warn(`  ${group.map(task => `T${task.id} (line ${task.line})`).join(', ')}: ${group[0].work}`);
        }
        warn('Keep one allocation for the same work. Distinct work needs its actual distinction in the task text.');
        return 1;
    }

    log('\nEvery task id is allocated exactly once.');
    return 0;
}

// =====================================================================================================
// THE FINDING QUEUE (docs/findings.md)
//
// Same machine, second register, for the same reason: findings were being generated far faster than they
// were consumed, ~65 sat open with no owner, and the automated Codex reviews were not being read at all.
// A prose rule demonstrably failed to reach two agents mid-task, so the consumer is a file with a check.
//
// The verdict set is CLOSED and has THREE states. `OPEN` was deleted on purpose: CI cannot verify that a
// named owner exists, was told, or agreed, so an owned-OPEN row is unenforceable theatre. What CAN be
// verified is what this asserts.
//
// WHAT IS DELIBERATELY NOT ASSERTED HERE, because it cannot be: whether a reason is good, whether evidence
// is evidence rather than an opinion, and whether a Codex comment exists at all. The last one needs the
// network and lives in tools/findings-codex-coverage.mjs, which FAILS when it cannot reach the API.
// =====================================================================================================

// FOUR states, and the fourth one was added because the three-state set made the honest action impossible.
// SCHEDULED requires a task that is NOT done, so the instant the scheduled work landed, the finding row broke
// its own invariant and no legal verdict was left to move it to. Measured while seeding the register: three
// rows were already in that position and had to be written as ACCEPTED with the fixing commit buried in the
// reason, and F-009's false comment sat unfixable in a file that was being edited at the time.
//
// FIXED is not a reopening of OPEN. OPEN was deleted because CI cannot verify an owner. FIXED is checkable,
// and it is checked in two places rather than merely being present:
//   - here, its SHAPE: the evidence must OPEN with a commit sha and then say what changed.
//   - in tools/findings-fixed-commits.mjs, its SUBSTANCE: the commit must EXIST, and it must have TOUCHED the
//     file the row names. A sha-shaped string is not a sha, and a real sha that never touched the named file
//     is not evidence that the named defect was fixed.
export const VERDICTS = ['SCHEDULED', 'FIXED', 'ACCEPTED', 'REFUTED'];

// The evidence of a FIXED row must BEGIN with the sha. Requiring position rather than mere presence is what
// makes it parseable without guessing: prose about a fix can easily contain a second hex-looking token, and a
// rule that hunts anywhere would then have to pick one.
const FIXED_EVIDENCE = /^([0-9a-f]{7,40})\b([\s\S]*)$/;

// The escape hatch, made explicit so it cannot be taken silently. A fix that legitimately did not touch the
// file the row names (the defect was in a caller, the file was deleted, the remedy was elsewhere) is allowed,
// but it has to say so in these exact words and then explain. Silence is what is forbidden.
export const NOT_THE_NAMED_FILE = 'NOT THE NAMED FILE:';

// A finding row is a markdown table row whose first cell is the id. Table cells cannot contain `|`, so a
// stray pipe inside a claim shows up as the wrong cell count, which fails rather than silently reparsing
// the row into different columns.
const FINDING_ID = /^F-(\d{3,})$/;

// The blindness guard, and the reason this check cannot quietly stop reading the register. Any line that
// puts a finding id in leading position of a table row or a list row is a row this check MUST be able to
// parse. One it sees here but cannot parse above means it is only partly reading the file, which is worse
// than a bad row: it is a clean report over the part it happens to understand.
const FINDING_POSITION = /^[ \t]*(?:\||[-*+][ \t]*)[ \t]*[*_`~[]{0,3}(F-\d+)/;

// A REASON/EVIDENCE floor. Arbitrary by admission: it catches "ok", "n/a" and "-", and nothing more. It is
// not a quality judgement and must never be described as one.
const REASON_FLOOR = 40;
const PLACEHOLDERS = new Set(['n/a', 'na', 'tbd', 'todo', 'none', '-', '--', '?', 'see above', 'pending']);

// A SCHEDULED row's link cell must be EXACTLY one task id and nothing else. Allowing prose beside it would
// let a row read as scheduled while pointing at nothing, which is the shape being prevented.
const TASK_LINK = /^\[T(\d+)\]$/;

const SOURCE_CODEX = /^codex\/PR#(\d+)\/(\d+)$/;

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

// TERMINAL-STATE DETECTION IN TASKS.md, measured rather than guessed. This register writes a task's state as
// an ALL-CAPS word, usually bolded right after the title: `- [T164] Connections round — **MERGED 2026-07-24`
// and `- [T11] ~~...~~ **CLOSED`. Measured on the register at a29d0abf, exactly three of 49 definition rows
// carry a caps terminal word on the definition line: T164 (**MERGED), T11 (**CLOSED) and T191, whose row
// reads `**PROCESS DEFECT, NOW CLOSED AT SOURCE` — a caps CLOSED that is describing something else.
//
// So bold-position is the state, and a bare caps word is AMBIGUOUS. Both fail when a finding points at the
// task, and they fail with different messages, because "your task is done" and "I cannot tell whether your
// task is done" are different problems and only the second one is the check's fault. Lower-case "closed"
// and "done" in prose are not matched at all, which is why these are case-sensitive.
//
// The other half of not-done is structural and needs no pattern: this register DELETES a task's definition
// row when it ships and leaves only a dated citation in the append-only DONE log, so a shipped task fails
// the "resolves in TASKS.md" test before any of this runs.
const TERMINAL = String.raw`(?:DONE|MERGED|SHIPPED|COMPLETE|COMPLETED|CLOSED|LANDED|WITHDRAWN|SUPERSEDED|ABANDONED)`;
const TERMINAL_STATE = new RegExp(String.raw`\*\*[ \t~]*${TERMINAL}\b`);
const TERMINAL_WORD = new RegExp(String.raw`\b${TERMINAL}\b`);

function splitRow(line) {
    const trimmed = line.trim();
    if (!trimmed.startsWith('|')) return null;
    let body = trimmed.slice(1);
    if (body.endsWith('|')) body = body.slice(0, -1);
    return body.split('|').map(cell => cell.trim());
}

export function analyseFindings(text) {
    const lines = text.split(/\r?\n/);

    const rows = [];              // { id, source, claim, site, verdict, link, date, line }
    const malformed = [];         // rows that look like findings but do not have seven cells
    const unrecognised = [];      // finding ids in leading position that no rule above parsed

    lines.forEach((line, index) => {
        const at = index + 1;
        const cells = splitRow(line);
        if (cells && FINDING_ID.test(cells[0])) {
            if (cells.length !== 7) {
                malformed.push({ line: at, id: cells[0], cells: cells.length });
                return;
            }
            const [id, source, claim, site, verdict, link, date] = cells;
            rows.push({ id, source, claim, site, verdict, link, date, line: at });
            return;
        }
        const leading = FINDING_POSITION.exec(line);
        if (leading) unrecognised.push({ line: at, id: leading[1], text: line.trim().slice(0, 120) });
    });

    const byId = new Map();
    for (const row of rows) {
        if (!byId.has(row.id)) byId.set(row.id, []);
        byId.get(row.id).push(row.line);
    }
    const duplicates = [...byId.entries()].filter(([, at]) => at.length > 1).sort();

    return { rows, duplicates, malformed, unrecognised };
}

// Everything a SCHEDULED row needs to know about TASKS.md, keyed by task id. Built from the SAME definition
// rule the duplicate check uses, so the two cannot disagree about what a task row is.
export function taskIndex(tasksText) {
    const lines = tasksText.split(/\r?\n/);
    const index = new Map();

    lines.forEach((line, i) => {
        const definition = DEFINITION.exec(line);
        if (!definition) return;
        const id = Number(definition[1]);
        if (index.has(id)) return;           // the duplicate check owns that failure; take the first row
        index.set(id, { line: i + 1, definitionLine: line, block: '' });
    });

    // The block is the definition row plus its continuation lines: everything up to the next definition row
    // or the next heading. The back-reference may be written on any line of it.
    for (const [, entry] of index) {
        const out = [lines[entry.line - 1]];
        for (let i = entry.line; i < lines.length; i++) {
            if (DEFINITION.test(lines[i]) || /^#{1,6}[ \t]/.test(lines[i])) break;
            out.push(lines[i]);
        }
        entry.block = out.join('\n');
    }

    return index;
}

export function checkFindings(findingsText, tasksText) {
    const { rows, duplicates, malformed, unrecognised } = analyseFindings(findingsText);
    const tasks = taskIndex(tasksText);
    const failures = [];

    if (rows.length === 0)
        failures.push('No finding rows found at all. The register format changed and this check is now blind, ' +
            'which is worse than a bad row. Fix the pattern, do not delete the check.');

    for (const row of malformed)
        failures.push(`line ${row.line}: ${row.id} has ${row.cells} cells, not 7. A pipe inside a cell splits ` +
            'the row into the wrong columns, so this fails instead of reading the wrong cell as a verdict.');

    for (const row of unrecognised)
        failures.push(`line ${row.line}: a finding id leads this row but the row rule could not parse it, so the ` +
            `check is only partly reading the register: ${row.text}`);

    for (const [id, at] of duplicates)
        failures.push(`${id} is defined ${at.length} times, on lines ${at.join(', ')}. One id means one finding.`);

    for (const row of rows) {
        const where = `line ${row.line} (${row.id})`;

        if (!VERDICTS.includes(row.verdict))
            failures.push(`${where}: verdict "${row.verdict}" is not in the closed set ${VERDICTS.join(' / ')}. ` +
                'There is no OPEN state: a finding gets a verdict when it is filed, because arrival is triage.');

        if (row.source.length === 0) failures.push(`${where}: the source cell is empty.`);
        // ANY mention of codex must take the keyed form. Testing for the `codex/` prefix was not enough: a bare
        // `codex`, or `codex#292`, sailed straight past it and that is the cheapest possible way to write a row
        // that looks like a filed automated finding while being unenumerable. Caught by this file's own test.
        else if (/codex/i.test(row.source) && !SOURCE_CODEX.test(row.source))
            failures.push(`${where}: source "${row.source}" is not codex/PR#<n>/<comment-id>. Identity is per ` +
                'FINDING, not per PR: keyed on a PR number alone, twenty findings collapse into one row and ' +
                'nothing can see it.');

        if (row.claim.length < 20) failures.push(`${where}: the claim is empty or too short to be a claim.`);
        if (row.site.length === 0) failures.push(`${where}: the file:line cell is empty.`);
        if (!ISO_DATE.test(row.date)) failures.push(`${where}: the date "${row.date}" is not YYYY-MM-DD.`);

        if (row.verdict === 'SCHEDULED') {
            const link = TASK_LINK.exec(row.link);
            if (!link) {
                failures.push(`${where}: SCHEDULED needs its link cell to be exactly one task id, like [T195]. ` +
                    `Found "${row.link}".`);
                continue;
            }
            const taskId = Number(link[1]);
            const task = tasks.get(taskId);
            if (!task) {
                failures.push(`${where}: SCHEDULED names T${taskId}, which has no definition row in TASKS.md. ` +
                    'A shipped task loses its definition row and keeps only a dated DONE-log citation, so this ' +
                    'is also what a finding scheduled against finished work looks like.');
                continue;
            }
            if (TERMINAL_STATE.test(task.definitionLine))
                failures.push(`${where}: SCHEDULED names T${taskId}, whose row at TASKS.md:${task.line} is in a ` +
                    'DONE state. Scheduling work against a finished task is how a queue drains without anything ' +
                    'being done.');
            else if (TERMINAL_WORD.test(task.definitionLine))
                failures.push(`${where}: SCHEDULED names T${taskId}, and this check cannot tell whether ` +
                    `TASKS.md:${task.line} is done: it carries a capitalised state word that is not in bold ` +
                    'state position. Rewrite that row so its state is unambiguous. An unreadable state is the ' +
                    "check's problem to report, not to guess at.");

            if (!task.block.includes(row.id))
                failures.push(`${where}: TASKS.md:${task.line} (T${taskId}) does not mention ${row.id}. The link ` +
                    'must be BIDIRECTIONAL: a one-way pointer is decorative, and it is the back-reference that ' +
                    'makes "scheduled" impossible to fake.');
        } else if (row.verdict === 'FIXED') {
            const evidence = FIXED_EVIDENCE.exec(row.link);
            if (!evidence) {
                failures.push(`${where}: FIXED must open its evidence cell with the commit sha that fixed it, ` +
                    `like "2eaaf95d it did the thing". Found "${row.link}". A verdict that says the defect is ` +
                    'gone has to name the change that removed it, and tools/findings-fixed-commits.mjs then ' +
                    'proves that commit exists and touched the file this row names.');
            } else if (evidence[2].trim().length < 20) {
                failures.push(`${where}: FIXED names a commit and then says nothing. Follow the sha with what ` +
                    'changed, so the row is readable without running git.');
            }
            // A FIXED row lands in a LATER commit than the fix, necessarily: it names a sha, and a commit
            // cannot contain its own. That is a property of the state, not a workaround, and it is written down
            // in docs/findings.md so nobody tries to collapse the two.
        } else if (row.verdict === 'ACCEPTED' || row.verdict === 'REFUTED') {
            const what = row.verdict === 'ACCEPTED' ? 'reason' : 'evidence';
            const text = row.link;
            if (text.length === 0 || PLACEHOLDERS.has(text.toLowerCase()))
                failures.push(`${where}: ${row.verdict} requires a non-empty ${what}, and "${text}" is not one. ` +
                    `${row.verdict} is a terminal state, so the ${what} is the only thing left explaining it.`);
            else if (TASK_LINK.test(text))
                failures.push(`${where}: ${row.verdict} carries a task id where its ${what} belongs. A task id is ` +
                    'not a reason, and a terminal verdict pointing at future work is a contradiction.');
            else if (text.length < REASON_FLOOR)
                failures.push(`${where}: the ${what} is ${text.length} characters, below the ${REASON_FLOOR}` +
                    '-character floor. The floor only catches placeholders; it is not a quality judgement.');
        }
    }

    return { rows, failures, verdicts: VERDICTS };
}

export function mainFindings(findingsPath, tasksPath, log = console.log, warn = console.error) {
    const { rows, failures } = checkFindings(readFileSync(findingsPath, 'utf8'), readFileSync(tasksPath, 'utf8'));

    const counts = new Map(VERDICTS.map(v => [v, 0]));
    for (const row of rows) if (counts.has(row.verdict)) counts.set(row.verdict, counts.get(row.verdict) + 1);

    log(`${findingsPath}`);
    log(`  finding rows:             ${rows.length}`);
    for (const [verdict, n] of counts) log(`  ${(verdict + ':').padEnd(25)}${n}`);
    log(`  problems:                 ${failures.length}`);

    if (failures.length > 0) {
        warn('\nThe finding queue does not hold:');
        for (const failure of failures) warn(`  ${failure}`);
        warn('\nThis register exists because findings were being produced far faster than they were consumed.');
        warn('Fix the row. Widening the check is how the queue becomes theatre.');
        return 1;
    }

    log('\nEvery finding is filed once, carries a verdict from the closed set, and every SCHEDULED row is');
    log('linked to a live task in both directions.');
    return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
    const tasksPath = process.argv[2] ?? resolve(repoRoot, 'TASKS.md');
    const findingsPath = process.argv[3] ?? resolve(repoRoot, 'docs/findings.md');
    // Both registers, one invocation, and the exit code is the OR: a clean task register must not be able to
    // report success over a broken finding queue.
    const tasksCode = main(tasksPath);
    console.log('');
    const findingsCode = mainFindings(findingsPath, tasksPath);
    process.exit(tasksCode || findingsCode);
}
