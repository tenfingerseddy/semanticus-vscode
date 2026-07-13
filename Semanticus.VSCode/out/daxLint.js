"use strict";
// Pure, dependency-free DAX analysis for the editor's diagnostics — no vscode import, so it is unit-testable
// in plain node (see tools/daxlint-test). extension.ts converts the offset-based RawDiag[] into vscode
// Diagnostics. Deliberately conservative: it flags only HIGH-confidence problems against the live model's
// symbols (unbalanced () [] , unknown quoted table, unknown column on a known table, a [ref] that is neither a
// measure nor any column) so squiggles are trustworthy, not noisy. Unknown *functions* are NOT flagged (the
// function list is non-exhaustive) and unknown *unquoted* tables are skipped (could be a VAR).
Object.defineProperty(exports, "__esModule", { value: true });
exports.maskDax = maskDax;
exports.extractDaxSymbols = extractDaxSymbols;
exports.varDefinition = varDefinition;
exports.collectVars = collectVars;
exports.analyzeDax = analyzeDax;
// Replace string literals ("…", "" escapes), and // -- /* */ comments with spaces (length + newlines
// preserved so offsets stay valid). When maskIdents is true, also blank the interior of '…' quoted
// identifiers — used for the delimiter-balance pass so brackets inside an identifier never count.
function maskDax(text, maskIdents) {
    const a = text.split('');
    const n = a.length;
    const blank = (s, e) => { for (let k = s; k < e && k < n; k++)
        if (a[k] !== '\n' && a[k] !== '\r')
            a[k] = ' '; };
    let i = 0;
    while (i < n) {
        const c = text[i];
        if (c === '"') {
            let j = i + 1;
            while (j < n) {
                if (text[j] === '"') {
                    if (text[j + 1] === '"') {
                        j += 2;
                        continue;
                    }
                    break;
                }
                j++;
            }
            blank(i, j + 1);
            i = j + 1;
            continue;
        }
        if (c === "'") {
            let j = i + 1;
            while (j < n) {
                if (text[j] === "'") {
                    if (text[j + 1] === "'") {
                        j += 2;
                        continue;
                    }
                    break;
                }
                j++;
            }
            if (maskIdents)
                blank(i, j + 1);
            i = j + 1;
            continue;
        }
        if (c === '/' && text[i + 1] === '/') {
            let j = i + 2;
            while (j < n && text[j] !== '\n')
                j++;
            blank(i, j);
            i = j;
            continue;
        }
        if (c === '-' && text[i + 1] === '-') {
            let j = i + 2;
            while (j < n && text[j] !== '\n')
                j++;
            blank(i, j);
            i = j;
            continue;
        }
        if (c === '/' && text[i + 1] === '*') {
            let j = i + 2;
            while (j < n && !(text[j] === '*' && text[j + 1] === '/'))
                j++;
            blank(i, Math.min(j + 2, n));
            i = j + 2;
            continue;
        }
        i++;
    }
    return a.join('');
}
function balance(masked) {
    const out = [];
    const stack = [];
    const opener = { ')': '(', ']': '[' };
    for (let i = 0; i < masked.length; i++) {
        const c = masked[i];
        if (c === '(' || c === '[')
            stack.push({ ch: c, pos: i });
        else if (c === ')' || c === ']') {
            const top = stack.pop();
            if (!top || top.ch !== opener[c])
                out.push({ start: i, end: i + 1, message: `Unmatched '${c}'`, severity: 'error' });
        }
    }
    for (const s of stack)
        out.push({ start: s.pos, end: s.pos + 1, message: `Unclosed '${s.ch}'`, severity: 'error' });
    return out;
}
function references(masked, idx) {
    const out = [];
    // 'Table' optionally followed by [Column]
    const reTbl = /'((?:[^']|'')*)'(\s*\[([^\]]*)\])?/g;
    let m;
    while ((m = reTbl.exec(masked))) {
        const tableRaw = m[1].replace(/''/g, "'");
        const tlow = tableRaw.toLowerCase();
        if (!idx.tables.has(tlow)) {
            out.push({ start: m.index, end: m.index + m[1].length + 2, message: `Unknown table '${tableRaw}'`, severity: 'warning' });
            continue; // can't trust a column on an unknown table
        }
        if (m[2] && m[3]) {
            const col = m[3];
            const cols = idx.columnsByTable.get(tlow);
            if (cols && !cols.has(col.toLowerCase())) {
                const colStart = m.index + (m[0].length - m[2].length) + m[2].indexOf('[') + 1;
                out.push({ start: colStart, end: colStart + col.length, message: `Table '${tableRaw}' has no column '${col}'`, severity: 'warning' });
            }
        }
    }
    // Unquoted Table[Column] — only validate the column when Table is a KNOWN table (else it may be a VAR, skip).
    const reUnq = /(?<![\w.'\]])([A-Za-z_]\w*)\[([^\]]*)\]/g;
    while ((m = reUnq.exec(masked))) {
        const tlow = m[1].toLowerCase();
        if (!idx.tables.has(tlow) || !m[2])
            continue;
        const cols = idx.columnsByTable.get(tlow);
        if (cols && !cols.has(m[2].toLowerCase())) {
            const colStart = m.index + m[1].length + 1;
            out.push({ start: colStart, end: colStart + m[2].length, message: `Table '${m[1]}' has no column '${m[2]}'`, severity: 'warning' });
        }
    }
    // Standalone [Name] — a measure or a contextual column ref. Flag only when it matches NEITHER.
    const reBare = /(?<![\w.'\]])\[([^\]]*)\]/g;
    while ((m = reBare.exec(masked))) {
        const name = m[1];
        if (!name)
            continue;
        const low = name.toLowerCase();
        if (!idx.measures.has(low) && !idx.allColumns.has(low)) {
            out.push({ start: m.index, end: m.index + m[0].length, message: `Unknown measure or column '[${name}]'`, severity: 'warning' });
        }
    }
    return out;
}
/// Outline symbols for a DAX expression: each `VAR <name> = …` and the `RETURN` clause, with offset ranges
/// (the editor maps them to positions for a DocumentSymbolProvider → Ctrl+Shift+O + breadcrumbs). Strings and
/// comments are masked first so a VAR/RETURN inside them isn't surfaced. A flat expression yields no symbols.
function extractDaxSymbols(text) {
    const masked = maskDax(text, false);
    const raw = [];
    const re = /\b(VAR|RETURN)\b/gi;
    let m;
    while ((m = re.exec(masked))) {
        if (m[1].toUpperCase() === 'VAR') {
            const nameRe = /(\s+)([A-Za-z_]\w*)/y;
            nameRe.lastIndex = m.index + m[0].length;
            const nm = nameRe.exec(masked);
            if (nm && nm.index === m.index + m[0].length) {
                const nameStart = nm.index + nm[1].length;
                raw.push({ kind: 'var', name: nm[2], kwStart: m.index, nameStart, nameEnd: nameStart + nm[2].length });
            }
        }
        else {
            raw.push({ kind: 'return', name: 'RETURN', kwStart: m.index, nameStart: m.index, nameEnd: m.index + m[0].length });
        }
    }
    return raw.map((s, i) => ({
        name: s.name, kind: s.kind,
        nameStart: s.nameStart, nameEnd: s.nameEnd,
        fullStart: s.kwStart,
        fullEnd: i + 1 < raw.length ? raw[i + 1].kwStart : text.length, // up to the next clause, else end of doc
    }));
}
/// The right-hand side of a `VAR <name> = …` declaration (for hovering a VAR reference), or null if `name`
/// isn't a VAR in this text. Returns the original (unmasked) RHS text, trimmed, up to the next clause.
function varDefinition(text, name) {
    const v = extractDaxSymbols(text).find((s) => s.kind === 'var' && s.name.toLowerCase() === name.toLowerCase());
    if (!v)
        return null;
    const decl = text.slice(v.fullStart, v.fullEnd);
    const eq = decl.indexOf('='); // first '=' is the assignment ("VAR name" has none)
    return eq >= 0 ? decl.slice(eq + 1).trim() : null;
}
/// Names declared by `VAR <name> = …` in the given text (e.g. the text before the cursor) — for VAR-name
/// completion. Strings/comments are masked first so a "VAR" inside them isn't picked up.
function collectVars(text) {
    const masked = maskDax(text, false);
    const re = /\bVAR\s+([A-Za-z_]\w*)/gi;
    const out = new Set();
    let m;
    while ((m = re.exec(masked)))
        out.add(m[1]);
    return [...out];
}
/// Analyze a DAX expression/query. Delimiter balance always runs; reference checks run only when an index is
/// supplied (a model is loaded) — otherwise every name would falsely read as unknown.
function analyzeDax(text, idx) {
    const diags = balance(maskDax(text, true));
    if (idx)
        diags.push(...references(maskDax(text, false), idx));
    return diags.sort((a, b) => a.start - b.start);
}
//# sourceMappingURL=daxLint.js.map