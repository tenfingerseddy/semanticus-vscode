import { parseM } from './mlang';

// ===================================================================================================
// M step transforms — the "UI writes M for you" kernel (docs/pq-transforms-plan.md). Deterministic
// text transformation over the outermost let-chain: append a generated step, rename a step (binding +
// every reference), delete a step (references re-point to its predecessor — Power Query's semantics).
// There is NO cross-platform M engine, so correctness is guarded the honest way: every public
// transform re-parses the result offline (parseM) and returns {ok:false} rather than ever handing
// back invalid M. Save remains the caller's explicit act.
// ===================================================================================================

export interface StepSpan {
  name: string;          // unquoted step name
  expr: string;
  start: number;         // offset of the binding's first char (the name)
  end: number;           // offset AFTER the binding's expr (before the following ',' or 'in')
}
export interface LetShape {
  steps: StepSpan[];
  inPos: number;         // offset of the top-level 'in' keyword
  resultStart: number;   // offset of the result expression after 'in'
  resultEnd: number;
  result: string;        // the result expression text (usually the last step's name)
}

// ---- scanning (string/comment/quoted-id aware — the appliedSteps discipline, with spans) ------------

const isIdentStart = (c: string) => /[A-Za-z_]/.test(c);
const isIdentChar = (c: string) => /[A-Za-z0-9_.]/.test(c);

/** Scan the outermost let-block into spans. Returns null when the text isn't a single let…in… shape. */
export function letShape(m: string): LetShape | null {
  try {
    const s = m, n = s.length;
    let i = 0;
    const skipNonCode = (): boolean => {
      const c = s[i];
      if (c === '"') { i++; while (i < n) { if (s[i] === '"') { if (s[i + 1] === '"') i += 2; else { i++; break; } } else i++; } return true; }
      if (c === '#' && s[i + 1] === '"') { i += 2; while (i < n) { if (s[i] === '"') { if (s[i + 1] === '"') i += 2; else { i++; break; } } else i++; } return true; }
      if (c === '/' && s[i + 1] === '/') { while (i < n && s[i] !== '\n') i++; return true; }
      if (c === '/' && s[i + 1] === '*') { i += 2; while (i < n && !(s[i] === '*' && s[i + 1] === '/')) i++; i += 2; return true; }
      return false;
    };
    // find the first top-level `let`
    let letPos = -1, d0 = 0;
    while (i < n) {
      if (skipNonCode()) continue;
      const c = s[i];
      if ('([{'.includes(c)) { d0++; i++; continue; }
      if (')]}'.includes(c)) { d0--; i++; continue; }
      if (d0 === 0 && isIdentStart(c)) {
        let k = i; while (k < n && isIdentChar(s[k])) k++;
        if (s.slice(i, k) === 'let') { letPos = i; i = k; break; }
        i = k; continue;
      }
      i++;
    }
    if (letPos < 0) return null;

    const steps: StepSpan[] = [];
    let depth = 0, segStart = -1;
    const flush = (segEnd: number) => {
      if (segStart < 0) return;
      const raw = s.slice(segStart, segEnd);
      // split at the first top-level '=' (not ==, =>, <=, >=)
      let d = 0, eq = -1, j = 0;
      while (j < raw.length) {
        const c = raw[j];
        if (c === '"' || (c === '#' && raw[j + 1] === '"')) { // skip strings/quoted ids inside the segment
          const q = c === '#' ? j + 2 : j + 1; let k = q;
          while (k < raw.length) { if (raw[k] === '"') { if (raw[k + 1] === '"') k += 2; else { k++; break; } } else k++; }
          j = k; continue;
        }
        if ('([{'.includes(c)) d++; else if (')]}'.includes(c)) d--;
        else if (d === 0 && c === '=' && raw[j + 1] !== '=' && raw[j + 1] !== '>' && raw[j - 1] !== '<' && raw[j - 1] !== '>') { eq = j; break; }
        j++;
      }
      if (eq <= 0) { segStart = -1; return; }
      const nameRaw = raw.slice(0, eq).trim();
      const expr = raw.slice(eq + 1).trim();
      if (!nameRaw) { segStart = -1; return; }
      const name = nameRaw.startsWith('#"') ? nameRaw.slice(2, -1).replace(/""/g, '"') : nameRaw;
      // trim span to the non-whitespace extent
      let a = segStart; while (a < segEnd && /\s/.test(s[a])) a++;
      let b = segEnd; while (b > a && /\s/.test(s[b - 1])) b--;
      steps.push({ name, expr, start: a, end: b });
      segStart = -1;
    };

    i = letPos + 3; segStart = i;
    while (i < n) {
      if (skipNonCode()) continue;
      const c = s[i];
      if ('([{'.includes(c)) { depth++; i++; continue; }
      if (')]}'.includes(c)) { depth--; i++; continue; }
      if (depth === 0) {
        if (c === ',') { flush(i); i++; segStart = i; continue; }
        if (isIdentStart(c)) {
          let k = i; while (k < n && isIdentChar(s[k])) k++;
          if (s.slice(i, k) === 'in') {
            flush(i);
            const resultStart = k;
            let b = n; while (b > resultStart && /\s/.test(s[b - 1])) b--;
            let a = resultStart; while (a < b && /\s/.test(s[a])) a++;
            return { steps, inPos: i, resultStart: a, resultEnd: b, result: s.slice(a, b) };
          }
          i = k; continue;
        }
      }
      i++;
    }
    return null;
  } catch { return null; }
}

// ---- quoting / escaping ------------------------------------------------------------------------------

const M_KEYWORDS = new Set(['and', 'as', 'each', 'else', 'error', 'false', 'if', 'in', 'is', 'let', 'meta', 'not', 'null', 'or', 'otherwise', 'section', 'shared', 'then', 'true', 'try', 'type']);

/** A step/identifier reference: bare when it's a plain identifier, else the #"…" quoted form. */
export function mQuoteIdent(name: string): string {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(name) && !M_KEYWORDS.has(name) ? name : `#"${name.replace(/"/g, '""')}"`;
}
/** An M text literal. */
export function mString(s: string): string { return `"${(s ?? '').replace(/"/g, '""')}"`; }

// the two textual forms a reference to `name` can take in source
const refForms = (name: string) => [`#"${name.replace(/"/g, '""')}"`, ...(/^[A-Za-z_][A-Za-z0-9_]*$/.test(name) ? [name] : [])];

// Replace whole-token occurrences of a step reference OUTSIDE strings/comments. Quoted form is matched
// literally; the bare form only at identifier boundaries.
function replaceRefs(m: string, from: string, toRef: string): string {
  const s = m; let out = ''; let i = 0; const n = s.length;
  const quoted = `#"${from.replace(/"/g, '""')}"`;
  const bareOk = /^[A-Za-z_][A-Za-z0-9_]*$/.test(from);
  while (i < n) {
    const c = s[i];
    if (c === '"') { let k = i + 1; while (k < n) { if (s[k] === '"') { if (s[k + 1] === '"') k += 2; else { k++; break; } } else k++; } out += s.slice(i, k); i = k; continue; }
    if (c === '/' && s[i + 1] === '/') { let k = i; while (k < n && s[k] !== '\n') k++; out += s.slice(i, k); i = k; continue; }
    if (c === '/' && s[i + 1] === '*') { let k = i + 2; while (k < n && !(s[k] === '*' && s[k + 1] === '/')) k++; k += 2; out += s.slice(i, k); i = k; continue; }
    if (c === '#' && s[i + 1] === '"') {
      let k = i + 2; while (k < n) { if (s[k] === '"') { if (s[k + 1] === '"') k += 2; else { k++; break; } } else k++; }
      const tok = s.slice(i, k);
      out += tok === quoted ? toRef : tok;
      i = k; continue;
    }
    if (bareOk && isIdentStart(c) && (i === 0 || !isIdentChar(s[i - 1]))) {
      let k = i; while (k < n && isIdentChar(s[k])) k++;
      const tok = s.slice(i, k);
      out += tok === from ? toRef : tok;
      i = k; continue;
    }
    out += c; i++;
  }
  return out;
}

export type TransformResult = { ok: true; m: string; step?: string } | { ok: false; error: string };

async function validated(m: string, step?: string): Promise<TransformResult> {
  const p = await parseM(m);
  return p.ok ? { ok: true, m, step } : { ok: false, error: 'The generated M does not parse. Nothing was changed. ' + p.error };
}

// ---- step operations -----------------------------------------------------------------------------------

/** Append a generated step after the current result step and make it the new result. */
export async function appendStep(m: string, baseName: string, mkExpr: (prevRef: string) => string): Promise<TransformResult> {
  const shape = letShape(m);
  if (!shape || shape.steps.length === 0) return { ok: false, error: 'This M is not a single let…in… expression. Add steps in the editor instead.' };
  // the step the result points at (fall back to the last binding)
  const prev = shape.steps.find((st) => refForms(st.name).includes(shape.result.trim())) ?? shape.steps[shape.steps.length - 1];
  // unique name: "Removed Columns", "Removed Columns1", …
  let name = baseName; let idx = 1;
  while (shape.steps.some((st) => st.name === name)) name = baseName + idx++;
  const nameRef = mQuoteIdent(name);
  const expr = mkExpr(mQuoteIdent(prev.name));
  const last = shape.steps[shape.steps.length - 1];
  const out = m.slice(0, last.end) + `,\n    ${nameRef} = ${expr}` + m.slice(last.end, shape.resultStart) + nameRef + m.slice(shape.resultEnd);
  return validated(out, name);
}

/** Rename a step: the binding AND every reference (incl. the in-result). */
export async function renameStep(m: string, oldName: string, newName: string): Promise<TransformResult> {
  newName = (newName ?? '').trim();
  if (!newName) return { ok: false, error: 'A step needs a name.' };
  const shape = letShape(m);
  if (!shape) return { ok: false, error: 'This M is not a single let…in… expression.' };
  if (!shape.steps.some((st) => st.name === oldName)) return { ok: false, error: `No step named '${oldName}'.` };
  if (shape.steps.some((st) => st.name === newName)) return { ok: false, error: `A step named '${newName}' already exists.` };
  return validated(replaceRefs(m, oldName, mQuoteIdent(newName)));
}

/** Delete a step; references to it re-point to its predecessor (Power Query's delete semantics). */
export async function deleteStep(m: string, name: string): Promise<TransformResult> {
  const shape = letShape(m);
  if (!shape) return { ok: false, error: 'This M is not a single let…in… expression.' };
  const idx = shape.steps.findIndex((st) => st.name === name);
  if (idx < 0) return { ok: false, error: `No step named '${name}'.` };
  if (shape.steps.length === 1) return { ok: false, error: 'The only step cannot be deleted. Edit it instead.' };
  if (idx === 0) return { ok: false, error: 'The first step (the source) has no predecessor to re-point to. Edit it instead.' };
  const prevRef = mQuoteIdent(shape.steps[idx - 1].name);
  const span = shape.steps[idx];
  // remove the binding + ONE adjacent comma (the preceding one, since idx > 0)
  let a = span.start;
  while (a > 0 && /\s/.test(m[a - 1])) a--;
  if (m[a - 1] === ',') a--;
  const removed = m.slice(0, a) + m.slice(span.end);
  return validated(replaceRefs(removed, name, prevRef));
}

// ---- transform generators (each returns the appendStep call) --------------------------------------------

const cols = (names: string[]) => `{${names.map(mString).join(', ')}}`;

export const gen = {
  incrementalRefreshFilter: (m: string, col: string) => {
    // appendStep intentionally refuses a bare source. Incremental refresh is the one flow that owns a safe,
    // explicit wrapper: the original expression remains the Source binding and the filter is still appended.
    const source = /^\s*let\b/.test(m) ? m : `let\n    Source = ${m.trim()}\nin\n    Source`;
    return appendStep(source, 'Filtered for incremental refresh', (p) => {
      const c = `[${col.replace(/\]/g, ']]')}]`;
      return `Table.SelectRows(${p}, each ${c} >= RangeStart and ${c} < RangeEnd)`;
    });
  },
  removeColumns: (m: string, names: string[]) =>
    appendStep(m, 'Removed Columns', (p) => `Table.RemoveColumns(${p}, ${cols(names)})`),
  renameColumn: (m: string, from: string, to: string) =>
    appendStep(m, 'Renamed Columns', (p) => `Table.RenameColumns(${p}, {{${mString(from)}, ${mString(to)}}})`),
  changeType: (m: string, col: string, mType: string) =>
    appendStep(m, 'Changed Type', (p) => `Table.TransformColumnTypes(${p}, {{${mString(col)}, ${mType}}})`),
  removeDuplicates: (m: string, names: string[]) =>
    appendStep(m, 'Removed Duplicates', (p) => names.length ? `Table.Distinct(${p}, ${cols(names)})` : `Table.Distinct(${p})`),
  keepTopN: (m: string, n: number) =>
    appendStep(m, 'Kept First Rows', (p) => `Table.FirstN(${p}, ${Math.max(1, Math.floor(n))})`),
  sort: (m: string, col: string, descending: boolean) =>
    appendStep(m, 'Sorted Rows', (p) => `Table.Sort(${p}, {{${mString(col)}, Order.${descending ? 'Descending' : 'Ascending'}}})`),
  trimClean: (m: string, names: string[]) =>
    appendStep(m, 'Trimmed Text', (p) => `Table.TransformColumns(${p}, {${names.map((c) => `{${mString(c)}, each Text.Clean(Text.Trim(_)), type text}`).join(', ')}})`),
  replaceValues: (m: string, col: string, find: string, replaceWith: string) =>
    appendStep(m, 'Replaced Value', (p) => `Table.ReplaceValue(${p}, ${mString(find)}, ${mString(replaceWith)}, Replacer.ReplaceText, ${cols([col])})`),
  // filter: the value literal is caller-built (mString for text; raw for numbers) so number filters stay numeric
  filterRows: (m: string, col: string, op: 'equals' | 'notEquals' | 'contains' | 'beginsWith' | 'greater' | 'less' | 'nonEmpty', valueLiteral: string) =>
    appendStep(m, 'Filtered Rows', (p) => {
      const c = `[${col.replace(/\]/g, ']]')}]`;   // record-field access: ] escapes as ]]
      const body = op === 'equals' ? `${c} = ${valueLiteral}`
        : op === 'notEquals' ? `${c} <> ${valueLiteral}`
        : op === 'contains' ? `Text.Contains(${c}, ${valueLiteral})`
        : op === 'beginsWith' ? `Text.StartsWith(${c}, ${valueLiteral})`
        : op === 'greater' ? `${c} > ${valueLiteral}`
        : op === 'less' ? `${c} < ${valueLiteral}`
        : `${c} <> null and ${c} <> ""`;
      return `Table.SelectRows(${p}, each ${body})`;
    }),
};

/** The Change-Type picker's M type map (label → M type expression). */
export const M_TYPES: [string, string][] = [
  ['Text', 'type text'], ['Whole Number', 'Int64.Type'], ['Decimal Number', 'type number'],
  ['Currency', 'Currency.Type'], ['Percentage', 'Percentage.Type'], ['Date', 'type date'],
  ['Date/Time', 'type datetime'], ['Time', 'type time'], ['Duration', 'type duration'],
  ['True/False', 'type logical'], ['Binary', 'type binary'],
];
