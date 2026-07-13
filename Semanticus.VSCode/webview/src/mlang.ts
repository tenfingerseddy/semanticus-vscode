// M language support for the CodeMirror editors — all in-webview, no language server.
// Highlighting is a hand-written StreamLanguage; formatting + validity come from Microsoft's MIT, browser-safe
// @microsoft/powerquery-formatter / -parser. Autocomplete + hover-types live in manalysis.ts (the
// powerquery-language-services Analysis + the vendored M standard library) — see docs/powerquery-tab-plan.md.
import { StreamLanguage, HighlightStyle } from '@codemirror/language';
import { tags as t } from '@lezer/highlight';
import * as PQP from '@microsoft/powerquery-parser';
import * as PQF from '@microsoft/powerquery-formatter';

// The reserved M keywords (Language spec §2). `each`/`let`/`in`/`if`/… drive control flow + bindings.
const KEYWORDS = new Set([
  'and', 'as', 'each', 'else', 'error', 'false', 'if', 'in', 'is', 'let', 'meta',
  'not', 'null', 'or', 'otherwise', 'section', 'shared', 'then', 'true', 'try', 'type',
]);

interface MState { block: boolean; }

// A compact M tokenizer: comments, strings, #"quoted identifiers", #intrinsics (#date/#table/…), numbers,
// operators, and keyword-vs-identifier. Returns the classic CodeMirror token names, mapped to tags by mHighlight.
export const mLanguage = StreamLanguage.define<MState>({
  startState: () => ({ block: false }),
  token(stream, state) {
    if (state.block) {                                   // inside /* … */
      if (stream.match(/.*?\*\//)) state.block = false; else stream.skipToEnd();
      return 'comment';
    }
    if (stream.eatSpace()) return null;
    if (stream.match('//')) { stream.skipToEnd(); return 'comment'; }
    if (stream.match('/*')) { state.block = true; stream.match(/.*?\*\//) && (state.block = false); return 'comment'; }
    if (stream.match('#"')) { stream.match(/(?:[^"]|"")*"/); return 'variableName'; }   // quoted identifier
    if (stream.peek() === '"') { stream.next(); stream.match(/(?:[^"]|"")*"/); return 'string'; }
    if (stream.match(/#(?:date|datetime|datetimezone|duration|time|table|binary|sections|shared|infinity|nan)\b/i)) return 'keyword';
    if (stream.match(/0x[0-9a-fA-F]+|\d[\d.]*(?:[eE][+-]?\d+)?/)) return 'number';
    if (stream.match(/[A-Za-z_][A-Za-z0-9_.]*/)) return KEYWORDS.has(stream.current()) ? 'keyword' : 'variableName';
    if (stream.match(/<=|>=|<>|=>|\.\.\.|\.\.|[-+*/&=<>@]/)) return 'operator';
    stream.next();
    return null;
  },
  tokenTable: undefined,
});

export const mHighlight = HighlightStyle.define([
  { tag: t.keyword, color: 'var(--sem-accent)' },
  { tag: t.string, color: 'var(--sem-good)' },
  { tag: t.comment, color: 'var(--sem-muted)', fontStyle: 'italic' },
  { tag: t.number, color: 'var(--sem-warn)' },
  { tag: t.variableName, color: '#9cdcfe' },
  { tag: t.operator, color: 'var(--sem-fg)' },
]);

const errMsg = (e: unknown): string => {
  const m = (e as { message?: string })?.message;
  return (typeof m === 'string' && m) ? m : String(e);
};

// Pretty-print M (offline). Returns the formatted text, or an error message if the M doesn't lex/parse.
export async function formatM(text: string): Promise<{ ok: true; text: string } | { ok: false; error: string }> {
  try {
    const r = await PQF.tryFormat(PQF.DefaultSettings, text);
    return r.kind === PQP.ResultKind.Ok ? { ok: true, text: r.value } : { ok: false, error: errMsg(r.error) };
  } catch (e) { return { ok: false, error: errMsg(e) }; }
}

// Validity check (lex + parse). Used for the editor's "valid M" / error indicator.
export async function parseM(text: string): Promise<{ ok: true } | { ok: false; error: string }> {
  try {
    const task = await PQP.TaskUtils.tryLexParse(PQP.DefaultSettings, text);
    return PQP.TaskUtils.isOk(task) ? { ok: true } : { ok: false, error: errMsg((task as { error?: unknown }).error) };
  } catch (e) { return { ok: false, error: errMsg(e) }; }
}

// Applied-Steps outline: the ordered step bindings of the outermost `let … in …` block. A defensive single-pass
// scanner (depth-aware, string/comment-aware) — NOT a full parse — so it degrades to [] on anything unusual rather
// than throwing. Each step is `Name = expr`; the final `in <result>` is appended as the output step.
export function appliedSteps(m: string): { name: string; expr: string }[] {
  try {
    const s = m;
    const n = s.length;
    let i = 0, depth = 0;
    // advance past strings/comments/quoted-ids; returns true if it consumed something
    const skipNonCode = (): boolean => {
      const c = s[i];
      if (c === '"') { i++; while (i < n) { if (s[i] === '"') { if (s[i + 1] === '"') i += 2; else { i++; break; } } else i++; } return true; }
      if (c === '#' && s[i + 1] === '"') { i += 2; while (i < n) { if (s[i] === '"') { if (s[i + 1] === '"') i += 2; else { i++; break; } } else i++; } return true; }
      if (c === '/' && s[i + 1] === '/') { while (i < n && s[i] !== '\n') i++; return true; }
      if (c === '/' && s[i + 1] === '*') { i += 2; while (i < n && !(s[i] === '*' && s[i + 1] === '/')) i++; i += 2; return true; }
      return false;
    };
    // find the first top-level `let`
    const findKeyword = (kw: string): number => {
      let j = i, d = 0;
      while (j < n) {
        const c = s[j];
        if (c === '"' || (c === '#' && s[j + 1] === '"') || (c === '/' && (s[j + 1] === '/' || s[j + 1] === '*'))) { i = j; skipNonCode(); j = i; continue; }
        if ('([{'.includes(c)) d++;
        else if (')]}'.includes(c)) d--;
        else if (d === 0 && /[A-Za-z_]/.test(c)) {
          let k = j; while (k < n && /[A-Za-z0-9_.]/.test(s[k])) k++;
          if (s.slice(j, k) === kw) return j;
          j = k; continue;
        }
        j++;
      }
      return -1;
    };
    const letPos = findKeyword('let');
    if (letPos < 0) return [];
    i = letPos + 3;
    // walk bindings, splitting on top-level commas, until the matching top-level `in`
    const steps: { name: string; expr: string }[] = [];
    let buf = '';
    const flush = () => {
      const b = buf.trim(); buf = '';
      if (!b) return;
      // split name = expr at the first top-level '='
      let d = 0, eq = -1;
      for (let k = 0; k < b.length; k++) {
        const c = b[k];
        if ('([{'.includes(c)) d++; else if (')]}'.includes(c)) d--;
        else if (d === 0 && c === '=' && b[k + 1] !== '=' && b[k + 1] !== '>' && b[k - 1] !== '<' && b[k - 1] !== '>') { eq = k; break; }   // skip ==, =>, <=, >=
      }
      if (eq > 0) steps.push({ name: b.slice(0, eq).trim().replace(/^#"|"$/g, ''), expr: b.slice(eq + 1).trim() });
    };
    while (i < n) {
      if (skipNonCode()) continue;
      const c = s[i];
      if ('([{'.includes(c)) { depth++; buf += c; i++; continue; }
      if (')]}'.includes(c)) { depth--; buf += c; i++; continue; }
      if (depth === 0) {
        if (c === ',') { flush(); i++; continue; }
        // top-level `in` ends the let block
        if (/[A-Za-z_]/.test(c)) {
          let k = i; while (k < n && /[A-Za-z0-9_.]/.test(s[k])) k++;
          const w = s.slice(i, k);
          if (w === 'in') { flush(); const out = s.slice(k).trim(); if (out) steps.push({ name: '(result)', expr: out }); return steps; }
          buf += w; i = k; continue;
        }
      }
      buf += c; i++;
    }
    flush();
    return steps;
  } catch { return []; }
}
