"use strict";
// Pure, dependency-free offline DAX pretty-printer (no vscode import → unit-testable in plain node; see
// tools/daxlint-test/format.test.cjs). Tokenize → parse (calls / grouping parens / arg lists) → render with a
// fit-or-expand strategy: a call/group renders on one line when it fits maxLine, else its arguments break onto
// indented lines. VAR/RETURN always break (each VAR on its own line). Strings/comments are preserved verbatim.
// It is a heuristic — good for the common shapes — not a reimplementation of daxformatter.com (that's the
// online opt-in). Conventions follow DAX Formatter: a space between a function name and '(' and inside parens.
Object.defineProperty(exports, "__esModule", { value: true });
exports.formatDax = formatDax;
const PATTERNS = [
    ['str', /"(?:[^"]|"")*"/y],
    ['tbl', /'(?:[^']|'')*'/y],
    ['col', /\[[^\]]*\]/y],
    ['bc', /\/\*[\s\S]*?\*\//y],
    ['lc', /(?:\/\/|--)[^\n]*/y],
    ['num', /[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/y],
    ['op', /&&|\|\||<=|>=|<>|:=|[-+*/=<>&^]/y],
    ['comma', /,/y],
    ['lp', /\(/y],
    ['rp', /\)/y],
    ['id', /[A-Za-z_][A-Za-z0-9_.]*/y],
    ['other', /\S/y],
];
function tokenize(s) {
    const toks = [];
    let i = 0;
    while (i < s.length) {
        if (/\s/.test(s[i])) {
            i++;
            continue;
        }
        let matched = false;
        for (const [k, re] of PATTERNS) {
            re.lastIndex = i;
            const m = re.exec(s);
            if (m && m.index === i) {
                toks.push({ k, v: m[0] });
                i += m[0].length;
                matched = true;
                break;
            }
        }
        if (!matched)
            i++;
    }
    return toks;
}
function parseAtom(toks, pos) {
    const t = toks[pos];
    if (t.k === 'id' && toks[pos + 1] && toks[pos + 1].k === 'lp') {
        const r = parseArgs(toks, pos + 2);
        return { node: { call: { name: t, args: r.args } }, pos: r.pos };
    }
    if (t.k === 'lp') {
        const r = parseArgs(toks, pos + 1);
        return { node: { call: { name: null, args: r.args } }, pos: r.pos };
    }
    return { node: { tok: t }, pos: pos + 1 };
}
function parseArgs(toks, pos) {
    const args = [];
    let cur = [];
    while (pos < toks.length && toks[pos].k !== 'rp') {
        if (toks[pos].k === 'comma') {
            args.push(cur);
            cur = [];
            pos++;
            continue;
        }
        const r = parseAtom(toks, pos);
        cur.push(r.node);
        pos = r.pos;
    }
    if (pos < toks.length && toks[pos].k === 'rp')
        pos++;
    args.push(cur);
    return { args, pos };
}
function parseTop(toks) {
    let pos = 0;
    const nodes = [];
    while (pos < toks.length) {
        if (toks[pos].k === 'rp') {
            pos++;
            continue;
        } // tolerate a stray ')'
        const r = parseAtom(toks, pos);
        nodes.push(r.node);
        pos = r.pos;
    }
    return nodes;
}
const isKw = (n, w) => 'tok' in n && n.tok.k === 'id' && n.tok.v.toUpperCase() === w;
const hasVarReturn = (nodes) => nodes.some((n) => isKw(n, 'VAR') || isKw(n, 'RETURN'));
const emptyArgs = (args) => args.length === 1 && args[0].length === 0;
const isUnary = (t, prev) => t.k === 'op' && (t.v === '-' || t.v === '+') && (prev === null || prev.k === 'op' || prev.k === 'comma' || prev.k === 'lp');
// Separator between the previous token and the next token of kind `cur`.
function sep(prev, cur) {
    if (!prev)
        return '';
    if (cur === 'comma')
        return '';
    if (cur === 'col' && (prev.k === 'tbl' || prev.k === 'id' || prev.k === 'col'))
        return ''; // Sales[Amt], 'T'[Amt]
    return ' ';
}
// Inline (single-line) render of a sequence. Returns null when it cannot be inlined (a comment or VAR/RETURN).
function inlineSeq(nodes, o) {
    if (hasVarReturn(nodes))
        return null;
    let out = '';
    let prev = null;
    let hug = false; // previous token was a unary +/- → next token hugs it
    for (const node of nodes) {
        if ('tok' in node) {
            const t = node.tok;
            if (t.k === 'lc' || t.k === 'bc')
                return null;
            out += (hug ? '' : sep(prev, t.k)) + t.v;
            hug = isUnary(t, prev);
            prev = t;
        }
        else {
            const c = node.call;
            const pre = hug ? '' : sep(prev, c.name ? 'id' : 'lp');
            const head = c.name ? c.name.v + (o.spaceAfterFunction ? ' ' : '') : '';
            if (emptyArgs(c.args)) {
                out += pre + head + '()';
            }
            else {
                const parts = [];
                for (const a of c.args) {
                    const s = inlineSeq(a, o);
                    if (s === null)
                        return null;
                    parts.push(s);
                }
                out += pre + head + '( ' + parts.join(', ') + ' )';
            }
            hug = false;
            prev = { k: 'rp', v: ')' };
        }
    }
    return out;
}
const fits = (text, indent, o) => indent.length + text.length <= o.maxLine;
// Render a sequence, breaking onto multiple lines when it does not fit (recursively).
function renderSeq(nodes, indent, o) {
    const inline = inlineSeq(nodes, o);
    if (inline !== null && fits(inline, indent, o))
        return inline;
    if (hasVarReturn(nodes))
        return renderVarReturn(nodes, indent, o);
    let out = '';
    let prev = null;
    let hug = false;
    for (const node of nodes) {
        if ('tok' in node) {
            const t = node.tok;
            out += (hug ? '' : sep(prev, t.k)) + t.v;
            // a line comment runs to EOL — the next token must start a fresh line or it would be commented out
            if (t.k === 'lc') {
                out += '\n' + indent;
                prev = null;
                hug = false;
                continue;
            }
            hug = isUnary(t, prev);
            prev = t;
        }
        else {
            out += renderCall(node, indent, o, prev, hug);
            hug = false;
            prev = { k: 'rp', v: ')' };
        }
    }
    return out;
}
function renderCall(node, indent, o, prev, hug) {
    const c = node.call;
    const pre = hug ? '' : sep(prev, c.name ? 'id' : 'lp');
    const head = c.name ? c.name.v + (o.spaceAfterFunction ? ' ' : '') : '';
    if (emptyArgs(c.args))
        return pre + head + '()';
    const inline = inlineSeq([node], o);
    if (inline !== null && fits(pre + inline, indent, o))
        return pre + inline;
    const childIndent = indent + o.indent;
    const args = c.args.map((a) => childIndent + renderSeq(a, childIndent, o));
    return pre + head + '(\n' + args.join(',\n') + '\n' + indent + ')';
}
function renderVarReturn(nodes, indent, o) {
    const lines = [];
    let i = 0;
    while (i < nodes.length) {
        const node = nodes[i];
        if (isKw(node, 'VAR')) {
            const nameNode = nodes[i + 1];
            const name = nameNode && 'tok' in nameNode ? nameNode.tok.v : '';
            let j = i + 3; // skip VAR, name, '='
            const rhs = [];
            while (j < nodes.length && !isKw(nodes[j], 'VAR') && !isKw(nodes[j], 'RETURN')) {
                rhs.push(nodes[j]);
                j++;
            }
            const head = `VAR ${name} = `;
            const inline = inlineSeq(rhs, o);
            if (inline !== null && fits(inline, indent + head, o))
                lines.push(head + inline);
            else
                lines.push(`VAR ${name} =\n${indent}${o.indent}${renderSeq(rhs, indent + o.indent, o)}`);
            i = j;
        }
        else if (isKw(node, 'RETURN')) {
            const rhs = nodes.slice(i + 1);
            lines.push(`RETURN\n${indent}${o.indent}${renderSeq(rhs, indent + o.indent, o)}`);
            break;
        }
        else {
            const s = inlineSeq([node], o);
            if (s)
                lines.push(s);
            i++;
        }
    }
    return lines.join('\n' + indent);
}
/// Format a DAX expression. Returns the trimmed input if there is nothing to tokenize (defensive).
function formatDax(src, opts) {
    const o = {
        indent: opts?.indent ?? '    ',
        maxLine: opts?.maxLine ?? 60,
        spaceAfterFunction: opts?.spaceAfterFunction ?? true,
    };
    const toks = tokenize(src);
    if (toks.length === 0)
        return src.trim();
    return renderSeq(parseTop(toks), '', o).trim();
}
//# sourceMappingURL=daxFormat.js.map