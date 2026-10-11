// The style check for One Visual Language: finds every spacing, text size, corner and control height in the
// Studio source that is not one of the named tokens in webview/src/tokens.css.
//
//   node scripts/visual-style-scan.mjs            per-file counts against the baseline, worst first
//   node scripts/visual-style-scan.mjs --list F   every violation in file F, as file:line selector property value
//   node scripts/visual-style-scan.mjs --update   lower the baseline to today's counts (it never raises one)
//
// test/visual-style-check.test.mjs runs the same scan on every `npm test` and fails on any file that went up.
//
// WHAT IT READS. Every stylesheet the Studio and the Properties grid load (webview/src/*.css except tokens.css,
// which is the source of the values, and media/propgrid/*.css), and every webview/src/*.tsx: Tailwind classes in
// className, inline style={{...}} objects, and object literals holding CSS properties.
//
// WHAT PASSES. var(--sem-*) where tokens.css defines that name, 0, auto, inherit/initial/unset/revert,
// percentages, and calc()/min()/max()/clamp() built only from those and plain numbers. The allow-list, each
// with its reason:
//   1px and -1px  a hairline: an offset or inset equal to one 1px border, which no spacing step can express.
//   line-height 1 and normal  a control with a fixed height centres its text with flex; leading 1 removes the
//                  extra line box so the label does not push the control taller.
// Tailwind classes are read as the value they compile to. A named step passes when it lands on the approved
// scale (p-3 is 12, text-xs is 12, rounded-sm is 4), because Tailwind's steps are themselves generated from
// its --spacing token; an off-scale step (py-1.5, text-base, rounded) fails, and an arbitrary value in [...]
// passes only when it is a token (p-[var(--sem-gap)], or the p-(--sem-gap) form).
//
// DISCLOSED LIMITS, so a green run is not read as more than it is:
//   - the `font` shorthand is not parsed, so a size inside it is not seen;
//   - an inline style value held in a variable or computed by an expression is not evaluated;
//   - HTML strings built in .ts files (docrender.ts and similar) are not read;
//   - height is checked on controls only: CSS selectors naming a button, input, select, textarea or a
//     btn/chip/pill/seg/tab/control/field class, and <button>/<input>/<select>/<textarea> in JSX.
import { readFileSync, readdirSync, writeFileSync, existsSync } from 'node:fs';
import { join, relative, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

// The approved token sheet: spacing, grid and heights from One Visual Language (23 September 2026). tokens.css must define exactly these; the test checks it.
export const APPROVED_TOKENS = {
  '--sem-space-1': '4px', '--sem-space-2': '8px', '--sem-space-3': '12px', '--sem-space-4': '16px', '--sem-space-6': '24px', '--sem-space-8': '32px',
  '--sem-page-margin': 'var(--sem-space-6)', '--sem-gap': 'var(--sem-space-3)', '--sem-grid-columns': '12',
  '--sem-card-pad': 'var(--sem-space-4)', '--sem-inset-pad': 'var(--sem-space-3)', '--sem-stack': 'var(--sem-space-2)',
  '--sem-bar-h': '40px', '--sem-row': '32px', '--sem-row-2': '48px',
  '--sem-control-h': '28px', '--sem-control-h-sm': '24px', '--sem-chip-h': '20px',
  // Type and corners are Instrument's (look-and-feel direction B, picked 23 September 2026).
  '--sem-text-label': '11px', '--sem-lh-label': '16px', '--sem-text-meta': '12px', '--sem-lh-meta': '16px',
  '--sem-text-body': '13px', '--sem-lh-body': '20px', '--sem-text-title': '14px', '--sem-lh-title': '20px',
  '--sem-text-page': '22px', '--sem-lh-page': '28px', '--sem-text-grade': '44px', '--sem-lh-grade': '48px', '--sem-weight': '400', '--sem-weight-strong': '600',
  '--sem-radius-control': '3px', '--sem-radius-inner': '3px', '--sem-radius-card': '4px', '--sem-radius-chip': '2px',
  '--sem-hairline': '1px', '--sem-elev': 'none',
};

const SPACE_PX = new Set([0, 4, 8, 12, 16, 24, 32]);
const TEXT_PX = new Set([11, 12, 13, 14, 22, 44]);
const LEADING_PX = new Set([16, 20, 28, 48]);
const RADIUS_PX = new Set([0, 2, 3, 4, 9999]); // 9999 is rounded-full: how Tailwind writes a circle, like 50%
const CONTROL_PX = new Set([20, 24, 28]);

const CSS_PROPS = /^(padding(-(top|right|bottom|left|inline|block|inline-start|inline-end|block-start|block-end))?|margin(-(top|right|bottom|left|inline|block|inline-start|inline-end|block-start|block-end))?|gap|row-gap|column-gap|grid-gap|font-size|line-height|border(-(top|bottom|start|end)-(left|right|start|end))?-radius|height|min-height)$/;
const CONTROL_SELECTOR = /(^|[^\w-])(button|input|select|textarea)\b|btn|chip|pill|seg|(^|[^\w])tab\b|-tab\b|control|field/i;
const KEYWORDS = new Set(['0', 'auto', 'inherit', 'initial', 'unset', 'revert', 'none']);

export const formatViolation = (v) => `${v.file}:${v.line} ${v.selector} ${v.prop} ${v.value}`;

// Split at top-level whitespace, commas and slashes, keeping function calls whole.
function topLevelParts(value) {
  const parts = []; let depth = 0; let cur = '';
  for (const ch of value) {
    if (ch === '(') depth++;
    if (ch === ')') depth--;
    if (depth === 0 && /[\s,/]/.test(ch)) { if (cur) parts.push(cur); cur = ''; continue; }
    cur += ch;
  }
  if (cur) parts.push(cur);
  return parts;
}

// One value part. `prop` decides the extra allowances (line-height 1).
function partAllowed(part, prop, tokens) {
  const p = part.trim();
  if (!p) return true;
  if (KEYWORDS.has(p)) return true;
  if (/^-?[\d.]+%$/.test(p)) return true;
  if (/^-?1px$/.test(p)) return true;
  if (/line-height|lineHeight/.test(prop) && (p === '1' || p === 'normal')) return true;
  if (/^-?0(\.0+)?(px|rem|em)?$/.test(p)) return true;
  const v = /^-?var\(\s*(--[\w-]+)\s*(?:,(.*))?\)$/s.exec(p);
  if (v) {
    if (!tokens.has(v[1])) return false;
    return v[2] === undefined || topLevelParts(v[2]).every((x) => partAllowed(x, prop, tokens));
  }
  const f = /^-?(calc|min|max|clamp)\((.*)\)$/s.exec(p);
  if (f) {
    // Inside a function, bare numbers are multipliers and operators are syntax; every length must be a token.
    const inner = f[2].replace(/var\([^()]*\)/g, (m) => (partAllowed(m, prop, tokens) ? ' 0 ' : ' BAD '));
    if (/BAD/.test(inner)) return false;
    return !/[\d.]+(px|rem|em|vh|vw|ch|ex)\b/.test(inner.replace(/\b0(px|rem|em)\b/g, '0'));
  }
  return false;
}

export function valueAllowed(value, prop, tokens) {
  const v = value.replace(/!important/g, '').trim();
  return topLevelParts(v).every((part) => partAllowed(part, prop, tokens));
}

// Comments become spaces (newlines kept) so line numbers survive.
const blankComments = (text) => text.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '));

export function scanCss(text, file, tokens) {
  const src = blankComments(text);
  const out = [];
  const stack = [];
  let start = 0;
  const lineAt = (i) => src.slice(0, i).split('\n').length;
  const checkDecl = (from, to) => {
    const raw = src.slice(from, to);
    const colon = raw.indexOf(':');
    if (colon === -1) return;
    const prop = raw.slice(0, colon).trim().toLowerCase();
    if (prop.startsWith('--') || !CSS_PROPS.test(prop)) return;
    const selector = stack[stack.length - 1] ?? '';
    if ((prop === 'height' || prop === 'min-height') && !CONTROL_SELECTOR.test(selector)) return;
    const value = raw.slice(colon + 1).trim();
    if (valueAllowed(value, prop, tokens)) return;
    const lead = raw.length - raw.trimStart().length;
    out.push({ file, line: lineAt(from + lead), selector, prop, value });
  };
  let quote = null;
  for (let i = 0; i < src.length; i++) {
    const ch = src[i];
    if (quote) { if (ch === quote && src[i - 1] !== '\\') quote = null; continue; }
    if (ch === '"' || ch === "'") { quote = ch; continue; }
    if (ch === '{') { stack.push(src.slice(start, i).trim().replace(/\s+/g, ' ')); start = i + 1; }
    else if (ch === ';') { if (stack.length && !stack[stack.length - 1].startsWith('@')) checkDecl(start, i); start = i + 1; }
    else if (ch === '}') { if (stack.length && !stack[stack.length - 1].startsWith('@')) checkDecl(start, i); stack.pop(); start = i + 1; }
  }
  return out;
}

// ---- TSX ---------------------------------------------------------------------------------------------
// gap-x and gap-y come before gap: the first alternative that fits wins, and `gap` would read gap-x-1.5 as the value x-1.5.
const TW_SPACE = /^(p|px|py|pt|pr|pb|pl|ps|pe|m|mx|my|mt|mr|mb|ml|ms|me|gap-x|gap-y|gap|space-x|space-y)-(.+)$/;
const TW_TEXT = { xs: 12, sm: 14, base: 16, lg: 18, xl: 20, '2xl': 24, '3xl': 30, '4xl': 36, '5xl': 48, '6xl': 60 };
const TW_RADIUS = { '': 4, xs: 2, sm: 4, md: 6, lg: 8, xl: 12, '2xl': 16, '3xl': 24, full: 9999, none: 0 };
const TW_LEADING_NAMED = new Set(['tight', 'snug', 'normal', 'relaxed', 'loose']);
const CONTROL_TAGS = new Set(['button', 'input', 'select', 'textarea']);

// An arbitrary Tailwind value: [..] or (..). Passes only as a token.
function arbitraryAllowed(raw, prop, tokens) {
  if (raw.startsWith('(') && raw.endsWith(')')) {
    const inner = raw.slice(1, -1).replace(/^length:/, '');
    return inner.startsWith('--') ? tokens.has(inner) : false;
  }
  if (raw.startsWith('[') && raw.endsWith(']')) return valueAllowed(raw.slice(1, -1).replace(/^length:/, '').replace(/_/g, ' '), prop, tokens);
  return null;
}

function classAllowed(cls, isControl, tokens) {
  const bare = cls.replace(/^!/, '').replace(/^-/, '');
  let m = TW_SPACE.exec(bare);
  if (m) {
    const val = m[2];
    const arb = arbitraryAllowed(val, 'padding', tokens);
    if (arb !== null) return arb;
    if (val === 'px' || val === 'auto' || val === 'reverse') return true;
    if (/^[\d.]+$/.test(val)) return SPACE_PX.has(Number(val) * 4);
    return true; // not a spacing value Tailwind knows as a length (e.g. a fraction); not ours to judge
  }
  m = /^text-(.+)$/.exec(bare);
  if (m) {
    const val = m[1].split('/')[0];
    const arb = arbitraryAllowed(val, 'font-size', tokens);
    if (arb !== null) return arb;
    if (val in TW_TEXT) return TEXT_PX.has(TW_TEXT[val]);
    return true; // a colour, alignment or wrapping utility
  }
  m = /^rounded(?:-(?:t|r|b|l|s|e|tl|tr|br|bl|ss|se|es|ee))?(?:-(.+))?$/.exec(bare);
  if (m) {
    const val = m[1] ?? '';
    const arb = arbitraryAllowed(val, 'border-radius', tokens);
    if (arb !== null) return arb;
    return val in TW_RADIUS ? RADIUS_PX.has(TW_RADIUS[val]) : true;
  }
  m = /^leading-(.+)$/.exec(bare);
  if (m) {
    const val = m[1];
    const arb = arbitraryAllowed(val, 'line-height', tokens);
    if (arb !== null) return arb;
    if (val === 'none') return true;
    if (TW_LEADING_NAMED.has(val)) return false;
    if (/^\d+$/.test(val)) return LEADING_PX.has(Number(val) * 4);
    return true;
  }
  m = /^(h|min-h)-(.+)$/.exec(bare);
  if (m && isControl) {
    const val = m[2];
    const arb = arbitraryAllowed(val, 'height', tokens);
    if (arb !== null) return arb;
    if (/^[\d.]+$/.test(val)) return CONTROL_PX.has(Number(val) * 4);
    if (val === 'px') return false;
    return true; // full, auto, screen, fit
  }
  return true;
}

// The element an offset belongs to: the nearest `<tag` before it that has not been closed by a `>` outside braces.
function enclosingTag(src, at) {
  for (let i = at; i >= 0; i--) {
    if (src[i] === '<' && /[A-Za-z]/.test(src[i + 1] ?? '')) return /^<([A-Za-z][\w.]*)/.exec(src.slice(i))[1];
    if (src[i] === '>' && src[i - 1] !== '=' ) {
      // `=>` inside an attribute expression is not the end of a tag; any other `>` before us closes one.
      let depth = 0;
      for (let j = i; j < at; j++) { if (src[j] === '{') depth++; else if (src[j] === '}') depth--; }
      if (depth <= 0) return null;
    }
  }
  return null;
}

// The balanced {...} starting at src[at] === '{'. Strings and template literals are skipped.
function balanced(src, at) {
  let depth = 0; let quote = null;
  for (let i = at; i < src.length; i++) {
    const c = src[i];
    if (quote) { if (c === '\\') { i++; continue; } if (c === quote) quote = null; continue; }
    if (c === '"' || c === "'" || c === '`') { quote = c; continue; }
    if (c === '{') depth++;
    else if (c === '}' && --depth === 0) return src.slice(at, i + 1);
  }
  return src.slice(at);
}

const JS_PROPS = new Set(['padding', 'paddingTop', 'paddingBottom', 'paddingLeft', 'paddingRight', 'paddingInline', 'paddingBlock',
  'paddingInlineStart', 'paddingInlineEnd', 'margin', 'marginTop', 'marginBottom', 'marginLeft', 'marginRight', 'marginInline', 'marginBlock',
  'marginInlineStart', 'marginInlineEnd', 'gap', 'rowGap', 'columnGap', 'borderRadius', 'fontSize', 'lineHeight', 'height', 'minHeight']);

// Comments are blanked (newlines kept) so a sentence like "gap: 6 between rows" in a comment is not a value.
const blankJsComments = (text) => blankComments(text).replace(/(^|[\s;{}(),])\/\/[^\n]*/g, (m, lead) => lead + m.slice(lead.length).replace(/[^\n]/g, ' '));

export function scanTsx(source, file, tokens) {
  const text = blankJsComments(source);
  const out = [];
  const lineAt = (i) => text.slice(0, i).split('\n').length;
  // className="..." | className={...}: every string literal the expression can produce.
  const classRe = /className=(?:"([^"]*)"|\{)/g;
  let m;
  while ((m = classRe.exec(text))) {
    const strings = m[1] !== undefined ? [m[1]] : [...balanced(text, m.index + 'className='.length).matchAll(/'([^']*)'|"([^"]*)"|`([^`]*)`/g)].map((x) => (x[1] ?? x[2] ?? x[3]).replace(/\$\{[^}]*\}/g, ' '));
    const tag = enclosingTag(text, m.index);
    const isControl = CONTROL_TAGS.has(tag ?? '');
    for (const s of strings) for (const raw of s.split(/\s+/).filter(Boolean)) {
      const cls = raw.replace(/^(?:[\w-]+:|\[[^\]]*\]:)+/, '');
      if (!classAllowed(cls, isControl, tokens)) out.push({ file, line: lineAt(m.index), selector: `<${tag ?? '?'}>`, prop: 'className', value: raw });
    }
  }
  // CSS-in-JS: `key: value` pairs. Inside style={{ }} the element is known; elsewhere it is an object literal.
  const styleSpans = [];
  const styleRe = /style=\{\{/g;
  while ((m = styleRe.exec(text))) {
    const body = balanced(text, m.index + 'style={'.length);
    styleSpans.push({ from: m.index, to: m.index + 'style={'.length + body.length, tag: enclosingTag(text, m.index) });
  }
  const pairRe = /\b([a-zA-Z]+)\s*:\s*(-?[\d.]+\b|'[^'\n]*'|"[^"\n]*")/g;
  while ((m = pairRe.exec(text))) {
    const key = m[1];
    if (!JS_PROPS.has(key)) continue;
    const span = styleSpans.find((s) => m.index > s.from && m.index < s.to);
    if ((key === 'height' || key === 'minHeight') && !(span && CONTROL_TAGS.has(span.tag ?? ''))) continue;
    const rawVal = m[2];
    const quoted = /^['"]/.test(rawVal);
    const val = quoted ? rawVal.slice(1, -1) : rawVal;
    const cssProp = key.replace(/[A-Z]/g, (c) => '-' + c.toLowerCase());
    const ok = quoted ? valueAllowed(val, cssProp, tokens) : (Number(val) === 0 || (key === 'lineHeight' && Number(val) === 1) || Math.abs(Number(val)) === 1);
    if (!ok) out.push({ file, line: lineAt(m.index), selector: span ? `<${span.tag ?? '?'}>` : 'object', prop: span ? 'style' : key, value: span ? `${key} ${val}` : val });
  }
  return out;
}

// ---- the repository ----------------------------------------------------------------------------------
export function loadTokenNames(root) {
  const text = blankComments(readFileSync(join(root, 'webview/src/tokens.css'), 'utf8'));
  return new Set([...text.matchAll(/(--sem-[\w-]+)\s*:/g)].map((x) => x[1]));
}

export function scannedFiles(root) {
  const list = [];
  for (const name of readdirSync(join(root, 'webview/src')).sort()) {
    if (name.endsWith('.css') && name !== 'tokens.css') list.push(`webview/src/${name}`);
    else if (name.endsWith('.tsx')) list.push(`webview/src/${name}`);
  }
  for (const name of readdirSync(join(root, 'media/propgrid')).sort()) if (name.endsWith('.css')) list.push(`media/propgrid/${name}`);
  return list;
}

export function scanRepo(root) {
  const tokens = loadTokenNames(root);
  const violations = [];
  const counts = {};
  for (const file of scannedFiles(root)) {
    const text = readFileSync(join(root, file), 'utf8');
    const v = file.endsWith('.css') ? scanCss(text, file, tokens) : scanTsx(text, file, tokens);
    counts[file] = v.length;
    violations.push(...v);
  }
  return { counts, violations };
}

export const BASELINE_PATH = 'scripts/visual-style-baseline.json';
export function readBaseline(root) {
  const p = join(root, BASELINE_PATH);
  return existsSync(p) ? JSON.parse(readFileSync(p, 'utf8')).files : {};
}

export function compareToBaseline(counts, baseline) {
  const failures = []; const lowered = [];
  for (const [file, n] of Object.entries(counts)) {
    if (!(file in baseline)) { if (n > 0) failures.push(`${file}: a new file with ${n} off-token value(s); new files start at zero`); continue; }
    const b = baseline[file];
    if (n > b) failures.push(`${file}: ${n} off-token value(s), baseline ${b}; the count may only go down`);
    else if (n < b) { failures.push(`${file}: ${n} off-token value(s), baseline ${b}; well done, now lower the baseline with \`node scripts/visual-style-scan.mjs --update\``); lowered.push(file); }
  }
  return { failures, lowered };
}

// ---- CLI -----------------------------------------------------------------------------------------------
const here = dirname(fileURLToPath(import.meta.url));
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = resolve(here, '..');
  const { counts, violations } = scanRepo(root);
  const baseline = readBaseline(root);
  const args = process.argv.slice(2);
  if (args[0] === '--list') {
    for (const v of violations.filter((x) => !args[1] || x.file === args[1] || x.file.endsWith('/' + args[1]))) console.log(formatViolation(v));
  } else if (args[0] === '--update') {
    const next = {}; const raised = [];
    for (const [file, n] of Object.entries(counts)) {
      if (file in baseline && n > baseline[file] && !args.includes('--seed')) { raised.push(`${file}: ${baseline[file]} -> ${n}`); next[file] = baseline[file]; }
      else if (!(file in baseline) && n > 0 && !args.includes('--seed')) { raised.push(`${file}: new file with ${n}`); }
      else next[file] = n;
    }
    const sorted = Object.fromEntries(Object.entries(next).sort(([a], [b]) => a.localeCompare(b)));
    const total = Object.values(sorted).reduce((a, b) => a + b, 0);
    writeFileSync(join(root, BASELINE_PATH), JSON.stringify({
      about: 'Off-token spacing, text size, corner and control-height values per file. May only go down. See scripts/visual-style-scan.mjs.',
      total, files: sorted,
    }, null, 1) + '\n');
    console.log(`baseline written: ${total} off-token value(s) in ${Object.values(sorted).filter((n) => n > 0).length} file(s)`);
    if (raised.length) { console.error(`refused to raise:\n  ${raised.join('\n  ')}`); process.exitCode = 1; }
  } else {
    const rows = Object.entries(counts).sort((a, b) => b[1] - a[1]);
    for (const [file, n] of rows) console.log(`${String(n).padStart(5)}  ${String(baseline[file] ?? '-').padStart(5)}  ${file}`);
    const total = rows.reduce((a, [, n]) => a + n, 0);
    console.log(`${String(total).padStart(5)}  total off-token values (count, baseline, file)`);
    const { failures } = compareToBaseline(counts, baseline);
    if (failures.length) { console.error(failures.join('\n')); process.exitCode = 1; }
  }
}
