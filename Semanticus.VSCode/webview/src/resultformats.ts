// A result the engine ran from typed DAX carries no formats (only an answer from Ask your data does), so a percent measure
// read 0.0068 under "Return Rate %" and the grid rounded it to 0.01 (Sol 6.1, 3.1, the hand-typed half). A column gets a
// measure's own format by name only when it is PROVEN to be that measure, and the proof has two halves:
//   the query: the column is an alias beside a bare measure reference ("Return Rate %", [Return Rate %]) among the direct
//     arguments of the table function that produces the output (ROW, SUMMARIZECOLUMNS, ADDCOLUMNS, SELECTCOLUMNS, or one of
//     TOPN, FILTER, CALCULATETABLE passing that table through). A pair inside a nested table is not the output, an alias that
//     appears twice says nothing, and a function this lookup does not know leaves every column as it was (Sol delta B2);
//   the model: the formats come from the model the numbers came from, which is the editing model only when the query
//     connection IS the editing model (Sol delta B4).
// Anything not proven shows the plain number: no invented percent.

import type { ResultSet } from './wire';

export interface MeasureFormat { name: string; formatString?: string | null }

/** The query with comments made blank, leaving text values and names alone. */
function withoutComments(text: string): string {
  let out = '';
  let i = 0;
  while (i < text.length) {
    const c = text[i];
    if (c === '"' || c === "'" || c === '[') {
      const close = c === '[' ? ']' : c;
      let j = i + 1;
      while (j < text.length) {
        if (text[j] === close) { if (text[j + 1] === close) { j += 2; continue; } break; }
        j++;
      }
      out += text.slice(i, j + 1);
      i = j + 1;
    } else if ((c === '/' && text[i + 1] === '/') || (c === '-' && text[i + 1] === '-')) {
      while (i < text.length && text[i] !== '\n') i++;
    } else if (c === '/' && text[i + 1] === '*') {
      const end = text.indexOf('*/', i + 2);
      i = end < 0 ? text.length : end + 2;
      out += ' ';
    } else out += text[i++];
  }
  return out;
}

/** DAX names ignore case, so every name this file compares is compared through this. */
const caseless = (name: string) => name.toLowerCase();
const unbracket = (name: string) => (/^\[[\s\S]*\]$/.test(name) ? name.slice(1, -1) : name).replace(/\]\]/g, ']');

/** The text with the inside of every text value and name blanked (same length), so a keyword or bracket search cannot land in one. */
function masked(text: string): string {
  let out = '';
  let i = 0;
  while (i < text.length) {
    const c = text[i];
    if (c === '"' || c === "'" || c === '[') {
      const close = c === '[' ? ']' : c;
      let j = i + 1;
      while (j < text.length) {
        if (text[j] === close) { if (text[j + 1] === close) { j += 2; continue; } break; }
        j++;
      }
      out += c + ' '.repeat(Math.max(0, j - i - 1)) + (j < text.length ? close : '');
      i = j + 1;
    } else out += text[i++];
  }
  return out;
}

/** The arguments of the call whose "(" is at `open`, cut at top-level commas; null when the call never closes. */
function argsAt(text: string, mask: string, open: number): { args: string[]; end: number } | null {
  const args: string[] = [];
  let depth = 0;
  let from = open + 1;
  for (let i = open; i < mask.length; i++) {
    const c = mask[i];
    if (c === '(' || c === '{') depth++;
    else if (c === ')' || c === '}') { depth--; if (depth === 0) { args.push(text.slice(from, i)); return { args, end: i }; } }
    else if (c === ',' && depth === 1) { args.push(text.slice(from, i)); from = i + 1; }
  }
  return null;
}

const TEXT_VALUE = /^\s*"((?:[^"]|"")*)"\s*$/;
const BARE_MEASURE = /^\s*\[((?:[^\]]|\]\])+)\]\s*$/;

/** Every `"alias", expression` pair among the direct arguments of the outermost table function, as alias -> the measure it
 *  bare-references (null when it is not a bare reference, or the alias appears more than once). Null when the output's
 *  producer is not one this lookup can read. */
function outputPairs(query: string, defined: Set<string>): Map<string, string | null> | null {
  const mask = masked(query);
  if ((mask.match(/\bEVALUATE\b/gi) ?? []).length !== 1) return null;
  let at = mask.search(/\bEVALUATE\b/i) + 'EVALUATE'.length;
  for (let depth = 0; depth < 8; depth++) {
    const head = /^\s*([A-Za-z_][\w.]*)\s*\(/.exec(mask.slice(at));
    if (!head) return null;
    const open = at + head[0].length - 1;
    const call = argsAt(query, mask, open);
    if (!call) return null;
    const name = head[1].toUpperCase();
    // A table that passes its columns through: look at the table it wraps, whose own columns the output keeps.
    const through = name === 'TOPN' ? 1 : name === 'FILTER' || name === 'CALCULATETABLE' ? 0 : -1;
    if (through >= 0) {
      if (call.args.length <= through) return null;
      // Re-locate that argument in the text, so the next round reads it with its own mask.
      let off = open + 1;
      for (let k = 0; k < through; k++) off += call.args[k].length + 1;
      at = off;
      continue;
    }
    const first = name === 'ROW' || name === 'SUMMARIZECOLUMNS' ? 0 : name === 'ADDCOLUMNS' || name === 'SELECTCOLUMNS' ? 1 : -1;
    if (first < 0) return null;
    // These two evaluate each pair once per row of their table, where a bare [Name] may be one of that table's own columns
    // rather than a measure. Over a table written out as an expression (a ROW, a SUMMARIZE, a FILTER) the columns are the
    // query's own and cannot be told from measures, so only a plain table name is read, and measuresNotShadowed drops any
    // measure whose name a column of the model also carries.
    // A name the query itself defines (DEFINE VAR, DEFINE TABLE) is a table the query wrote, never a model table, so its
    // columns are the query's own (Sol delta B2, the verifier's sibling).
    if (first === 1) {
      const table = /^\s*(?:'((?:[^']|'')+)'|([A-Za-z_][\w.]*))\s*$/.exec(call.args[0]);
      if (!table || defined.has(caseless((table[1] ?? table[2]).replace(/''/g, "'")))) return null;
    }
    const pairs = new Map<string, string | null>();
    for (let i = first; i < call.args.length; i++) {
      const alias = TEXT_VALUE.exec(call.args[i]);
      if (!alias || i + 1 >= call.args.length) continue;
      const name2 = caseless(alias[1].replace(/""/g, '"'));
      const bare = BARE_MEASURE.exec(call.args[i + 1]);
      pairs.set(name2, pairs.has(name2) || !bare ? null : bare[1].replace(/\]\]/g, ']'));
      i++;
    }
    return pairs;
  }
  return null;
}

export function withMeasureFormats(res: ResultSet, query: string | null | undefined, measures: MeasureFormat[]): ResultSet {
  if (!res || !query || !measures.length || res.columns.every((c) => c.format)) return res;
  const text = withoutComments(query);
  // Names the query defines itself. A DEFINE MEASURE replaces a model measure of the same name whatever its case (DAX names
  // are case-blind), a DEFINE COLUMN of that name hides it, and a DEFINE VAR or DEFINE TABLE is a table of the query's own.
  const own = new Set<string>();
  for (const m of text.matchAll(/\bMEASURE\s+(?:'(?:[^']|'')+'|[\w.]+)\s*\[((?:[^\]]|\]\])+)\]\s*=/gi)) own.add(caseless(m[1].replace(/\]\]/g, ']')));
  // A DEFINE COLUMN is a column of the query's own: a bare [Name] in the output may be that column and not the model measure of the
  // same name (a column reads before a measure), so it lends no format either (Sol delta, the verifier's second round, item 1).
  for (const m of text.matchAll(/\bCOLUMN\s+(?:'(?:[^']|'')+'|[\w.]+)\s*\[((?:[^\]]|\]\])+)\]\s*=/gi)) own.add(caseless(m[1].replace(/\]\]/g, ']')));
  const defined = new Set<string>();
  for (const m of text.matchAll(/\b(?:VAR|TABLE)\s+(?:'((?:[^']|'')+)'|([A-Za-z_][\w.]*))\s*=/gi)) defined.add(caseless((m[1] ?? m[2]).replace(/''/g, "'")));
  const byName = new Map(measures.filter((m) => m.formatString && m.formatString.trim()).map((m) => [caseless(m.name), m.formatString as string]));
  const named = outputPairs(text, defined);
  if (!named) return res;
  let found = false;
  const columns = res.columns.map((c) => {
    if (c.format) return c;
    const measure = named.get(caseless(unbracket(c.name)));
    const key = measure ? caseless(measure) : null;
    const format = key && !own.has(key) ? byName.get(key) : undefined;
    if (!format) return c;
    found = true;
    return { ...c, format };
  });
  return found ? { ...res, columns } : res;
}

/** What decides whether the editing model's own formats may be put on a result: see editingFormatsApply. */
export interface FormatContext {
  /** The engine's own word for how the editing model and the query connection relate (ConnectionContext.Relationship). */
  relationship?: string | null;
  /** The editing model has changes the connected model does not have, so a format may differ. */
  hasUnsavedChanges?: boolean;
}

/** The measures whose formats may be read onto a result sent NOW: the editing model's own, but only when the query connection
 *  IS the editing model (the engine says "sameInstance") and nothing is unsaved. Otherwise none, and the numbers show plain:
 *  another model's Margin is not this model's Margin (Sol delta B4). */
export function editingFormatsApply(measures: MeasureFormat[], ctx: FormatContext | null | undefined): MeasureFormat[] {
  if (!ctx || ctx.relationship !== 'sameInstance' || ctx.hasUnsavedChanges) return [];
  return measures;
}

/** The query model is a linked copy of the editing model (the default setup: edit the working copy, test on its runtime). */
export const queriesLinkedCopy = (relationship: string | null | undefined) => relationship === 'workingCopyAndLocalRuntime' || relationship === 'workingCopyAndPublished';

/** The DMV that reads the query model's own measure formats. */
export const MEASURE_FORMATS_DMV = 'SELECT [Name], [FormatString] FROM $SYSTEM.TMSCHEMA_MEASURES';

/** The query model's own measure formats, read from the DMV above; null when the answer is not that table. This is the proof the
 *  formats belong to the model the numbers came from (a linked copy's measure may differ from the editing model's). */
export function measureFormatsFromDmv(res: ResultSet | null | undefined): MeasureFormat[] | null {
  if (!res || res.error || !Array.isArray(res.columns) || !Array.isArray(res.rows)) return null;
  const at = (n: string) => res.columns.findIndex((c) => unbracket(c.name).toLowerCase() === n);
  const name = at('name'), format = at('formatstring');
  if (name < 0 || format < 0) return null;
  return res.rows.filter((r) => typeof r[name] === 'string').map((r) => ({ name: r[name] as string, formatString: typeof r[format] === 'string' ? (r[format] as string) : null }));
}

/** The formats a result was SENT with, noted when the query goes out and never re-read from whatever is on the page when the rows
 *  arrive: a connection switched during the run, or a model edit made since, cannot change which model the numbers came from. */
export interface SentFormats {
  /** The connection and model (not its revision) the query was sent to; the formats hold only while the page is still there. */
  scope: string;
  measures: MeasureFormat[];
}

/** The measures a result may be formatted with now: those noted at send, while the page is still on the connection it was sent to. */
export function sentFormatsNow(sent: SentFormats | null | undefined, scopeNow: string): MeasureFormat[] {
  return sent && sent.scope === scopeNow ? sent.measures : [];
}

/** The measures with no column anywhere in the model sharing their name: a bare [Name] inside a table's row context reads that
 *  table's column before a measure, so a name both kinds carry proves nothing about which one a query column is. */
export function measuresNotShadowed(measures: MeasureFormat[], columns: { name: string }[]): MeasureFormat[] {
  const taken = new Set(columns.map((c) => c.name.toLowerCase()));
  return measures.filter((m) => !taken.has(m.name.toLowerCase()));
}
