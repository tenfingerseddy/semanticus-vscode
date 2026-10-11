// The static scan behind test/solui-r3-inventory.test.mjs (join 11 solui, round 3): every text box in webview/src/*.tsx, and every draft scope prefix.
//
// A "text box" is a JSX <input> (not a checkbox, radio, range, file, button or submit), a <textarea>, a <DaxField>, a <DaxEditor> or a <MEditor>. Each gets a stable key,
// `file::Component::tag::signature`, where the signature is what the box shows (its value expression), else its aria label, else its placeholder. A second box with the same
// key is numbered `#2`. The key holds no line number, so an edit elsewhere in the file does not move it.
// Run `node test/helpers/box-inventory.mjs` to print the keys that have no class in test/box-inventory.json, as a starting manifest. The classes are a person's judgement; this prints
// a seed from simple evidence and says so.
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
export const SRC = join(here, '..', '..', 'webview', 'src');
export const MANIFEST = join(here, '..', 'box-inventory.json');
export const BOX_CLASSES = ['held', 'search', 'parent', 'transient', 'outage-deferred'];
export const LISTERS = ['orphan-drafts', 'named-lister', 'id-keyed', 'singleton', 'not-a-scope', 'unlisted'];
const NOT_TEXT = /^(checkbox|radio|range|file|button|submit|reset|color|image|hidden)$/;

/** The text of one JSX opening tag from `<` to its closing `>`: braces and quotes are tracked, so an arrow function in an attribute does not end it. */
function openingTag(src, from) {
  let depth = 0, quote = '';
  for (let i = from; i < src.length; i++) {
    const c = src[i];
    if (quote) { if (c === '\\') i++; else if (c === quote) quote = ''; continue; }
    if (depth === 0 && (c === '"' || c === '\'')) { quote = c; continue; }
    if (c === '{') { depth++; continue; }
    if (c === '}') { depth--; continue; }
    if (depth > 0 && (c === '"' || c === '\'' || c === '`')) { quote = c; continue; }
    if (depth === 0 && c === '>') return src.slice(from, i + 1);
  }
  return src.slice(from);
}
/** The value of one attribute of an opening tag: the text inside {...} or "...", else null. */
function attr(tag, name) {
  const at = new RegExp('[\\s]' + name + '=').exec(tag);
  if (!at) return null;
  let i = at.index + at[0].length;
  const open = tag[i];
  if (open === '"' || open === '\'') return tag.slice(i + 1, tag.indexOf(open, i + 1));
  if (open !== '{') return null;
  let depth = 0, quote = '';
  const start = i;
  for (; i < tag.length; i++) {
    const c = tag[i];
    if (quote) { if (c === '\\') i++; else if (c === quote) quote = ''; continue; }
    if (c === '"' || c === '\'' || c === '`') { quote = c; continue; }
    if (c === '{') depth++;
    else if (c === '}') { depth--; if (depth === 0) return tag.slice(start + 1, i); }
  }
  return null;
}
const squash = (s) => s.replace(/\s+/g, ' ').trim();

/** Every top-level declaration (a function or a const at column 0), with where its body ends (the next one). */
function components(src) {
  const out = [];
  const re = /^(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:function|const|class)\s+([A-Za-z_$][\w$]*)/gm;
  for (const m of src.matchAll(re)) out.push({ name: m[1], start: m.index });
  out.forEach((c, i) => { c.end = i + 1 < out.length ? out[i + 1].start : src.length; });
  return out;
}

export function scanBoxes() {
  const boxes = [];
  for (const file of readdirSync(SRC).filter((f) => f.endsWith('.tsx')).sort()) {
    const src = readFileSync(join(SRC, file), 'utf8');
    const comps = components(src);
    const seen = new Map();
    for (const m of src.matchAll(/<(input|textarea|DaxField|DaxEditor|MEditor)(?=[\s/>])/g)) {
      const before = src.lastIndexOf('\n', m.index);
      const lineHead = src.slice(before + 1, m.index);
      if (/^\s*(\/\/|\*|\/\*)/.test(lineHead)) continue;   // inside a comment
      const tag = openingTag(src, m.index);
      const type = attr(tag, 'type');
      if (m[1] === 'input' && type && NOT_TEXT.test(type.replace(/[{}'"`]/g, ''))) continue;
      const comp = comps.filter((c) => c.start <= m.index).pop() ?? { name: '(module)', start: 0, end: src.length };
      const sig = squash(attr(tag, 'value') ?? attr(tag, 'defaultValue') ?? attr(tag, 'ariaLabel') ?? attr(tag, 'aria-label') ?? attr(tag, 'placeholder') ?? attr(tag, 'data-testid') ?? '(no value)').slice(0, 70);
      const base = `${file}::${comp.name}::${m[1]}::${sig}`;
      const n = (seen.get(base) ?? 0) + 1;
      seen.set(base, n);
      boxes.push({ key: n === 1 ? base : `${base}#${n}`, file, component: comp.name, tag: m[1], type: type ?? null, signature: sig, line: src.slice(0, m.index).split('\n').length,
        body: src.slice(comp.start, comp.end), tagText: tag });
    }
  }
  return boxes;
}

/** Every `word|` string that starts a template or a quote in webview/src/*.ts(x): the candidates for a draft scope prefix (the first segment only). */
export function scanScopePrefixes() {
  const found = new Map();
  for (const file of readdirSync(SRC).filter((f) => /\.tsx?$/.test(f)).sort()) {
    const src = readFileSync(join(SRC, file), 'utf8');
    for (const m of src.matchAll(/['`]([a-z][a-z-]*\|)/g)) {
      const at = found.get(m[1]) ?? new Set();
      at.add(file);
      found.set(m[1], at);
    }
  }
  return found;
}

/** The files that draw `<OrphanDrafts prefix="x|...">` (a literal), and the files that draw one with a computed prefix. */
export function orphanListers() {
  const literal = new Map(), computed = [];
  for (const file of readdirSync(SRC).filter((f) => f.endsWith('.tsx')).sort()) {
    const src = readFileSync(join(SRC, file), 'utf8');
    for (const m of src.matchAll(/<OrphanDrafts\s+prefix=(?:"([^"]+)"|\{([^}]+)\})/g)) {
      if (m[1]) literal.set(m[1], file); else computed.push({ file, expr: m[2] });
    }
  }
  return { literal, computed };
}

/** A first guess at a box's class, from simple evidence. A person confirms it in the manifest. */
export function seedClass(b) {
  const sig = `${b.signature} ${b.tagText}`.toLowerCase();
  if (/type=["{']*(search)|search|filter|find /.test(sig)) return 'search';
  if (/useKeptText\([^)]*hold|heldDraft\(|useEditBase|persist:|useHoldDraft|draftStore\.set|\bhold=|holdAs|useHeld|HoldInput|NameInput/.test(b.body)) return 'held';
  if (/type=["{']*password|token|secret|api.?key/.test(sig)) return 'transient';
  if (/^(value|props\.|v\.value)/.test(b.signature)) return 'parent';
  return 'outage-deferred';
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  let manifest = { boxes: {}, scopes: {} };
  try { manifest = JSON.parse(readFileSync(MANIFEST, 'utf8')); } catch { /* none yet */ }
  const missing = scanBoxes().filter((b) => !(b.key in manifest.boxes));
  const out = {};
  for (const b of missing) out[b.key] = { class: seedClass(b) };
  console.log(JSON.stringify(out, null, 2));
  console.error(`${missing.length} boxes have no class (seeded from simple evidence, not judged).`);
}
