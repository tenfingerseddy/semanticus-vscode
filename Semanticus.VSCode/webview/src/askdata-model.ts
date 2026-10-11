// The Ask your data page's pure half: the wire shapes of ask_data, the sends, and every chip edit.
//
// The shapes mirror Semanticus.Engine/Query/DataAsk.cs and DataQuestionStore.cs, camelCased on the wire. A chip
// edit is a pure function over the parts the engine handed back, and the edited parts are sent back as parts:
// the engine then takes them as said ("you") and never calls Jev (LocalEngine.Query.cs, AnswerAsync). A typed
// question is read by Jev (runWords); every edit after that is parts again.
//
// Nothing here builds the question's DAX. The engine builds it from one template. The one piece of DAX written
// here is the single value a saved check asks, and it reuses the engine's own filter lines rather than
// rewriting them (checkDraftOf below).

import type { ExplainDossier } from './explainpanel';
import type { PivotConfig, WellField } from './daxwells';
import type { ResultSet } from './wire';

export interface DataAskFilter { column: string; op: string; values: string[] }
export interface DataAsk {
  question?: string | null;
  groupBy?: string[] | null;
  measures?: string[] | null;
  filters?: DataAskFilter[] | null;
  top?: number | null;
  topBy?: string | null;
  lowest?: boolean;
  sortBy?: string | null;
  descending?: boolean;
  maxRows?: number;
  run?: boolean;
}
export interface DataCheck { name: string; passed: boolean; line: string }
/** Open in Queries' other half (the Queries pre-flight, "Hand-offs"): the question's parts, its answer and its words,
 *  handed over in memory beside the one-use DAX text seed, so Queries shows the chips and the rows without asking
 *  again. Never put in the route or in persisted state: the answer's rows are the model's data. */
export interface QueriesHandoff { parts: DataAsk; answer: DataAnswer; words?: string }
export type Outcome = 'answered' | 'built' | 'needs-you' | 'refused' | 'error';
export interface DataAnswer {
  outcome: Outcome | string;
  reading?: string | null;
  /** What this answer shows, in one sentence, from the query that will run ("This shows Net Sales for each Region, where Year
   *  is 2025."). The first line of every answer, so anything that still gets through is visible. */
  statement?: string | null;
  readBy?: string | null;
  note?: string | null;
  /** What the answer assumed where the words left room, in one plain line ("Metric Category is not filtered."), when it
   *  assumed anything (K16). Each part it can change back is one of the others. */
  assumption?: string | null;
  others?: DataOther[];
  parts?: DataAsk | null;
  dax?: string | null;
  checks?: DataCheck[];
  unsure?: string[];
  columns?: { name: string; type?: string }[];
  /** The model's own format of each column's measure (null for a column that is not one), so a percent measure reads as a percent. */
  formats?: (string | null)[] | null;
  /** The explanation of the number, when the question asked WHY about one number: shown in place of the table. */
  why?: ExplainDossier | null;
  rows?: unknown[][];
  rowCount?: number;
  truncated?: boolean;
  elapsedMs?: number;
  error?: string | null;
  /** True when Jev was not asked only for want of permission the person can give: the note carries one Allow button (K19). */
  needsPermission?: boolean;
  approvalId?: string | null;
}
/** The other good reading when Jev was torn between two (Kane, 28 Sep): "Showing Net Sales. You may have meant
 *  Units.", with the whole question's parts for that reading. Asking them is one send of parts: no Jev call. */
export interface DataOther {
  line: string; instead: string; reading?: string | null; parts: DataAsk;
  /** True for the offer to run the reading that was stopped, as it is ("Show it anyway"): the parts, taken as said, no Jev call. */
  anyway?: boolean;
}
export interface DataQuestionRecord {
  id: string; askedAt: string; question?: string | null; reading?: string | null; readBy?: string | null;
  outcome?: string; parts?: DataAsk | null; dax?: string | null; rowCount?: number; origin?: string;
}
export interface DataQuestionHistory { questions: DataQuestionRecord[]; note?: string | null }

export const ASK_METHOD = 'askData';
export const HISTORY_METHOD = 'listDataQuestions';

type Send = (method: string, ...params: unknown[]) => Promise<unknown>;
const failed = (e: unknown): DataAnswer => ({ outcome: 'error', error: String((e as Error)?.message ?? e), checks: [], unsure: [] });

/** The parts only: the words, the run switch and the cap are the page's to set per send, not part of a question. */
export function cleanParts(p: DataAsk | null | undefined): DataAsk {
  return {
    measures: [...(p?.measures ?? [])], groupBy: [...(p?.groupBy ?? [])],
    filters: (p?.filters ?? []).map((f) => ({ column: f.column, op: f.op || 'is', values: [...(f.values ?? [])] })),
    top: p?.top ?? null, topBy: p?.topBy ?? null, lowest: !!p?.lowest, sortBy: p?.sortBy ?? null, descending: !!p?.descending,
  };
}

/** One ask from the chips: parts and nothing else, so it is read by "you" and Jev is never asked. */
export async function runAsk(send: Send, parts: DataAsk): Promise<DataAnswer> {
  try { return (await send(ASK_METHOD, cleanParts(parts))) as DataAnswer; } catch (e) { return failed(e); }
}

/** One ask in words. On Pro, Jev reads it into parts (readBy "jev"); with the point off for this model the engine
 *  answers in one line (SwitchedOffNote, or NotYetNote in a build where it is not on), and the page shows that line. */
export async function runWords(send: Send, question: string): Promise<DataAnswer> {
  try { return (await send(ASK_METHOD, { question })) as DataAnswer; } catch (e) { return failed(e); }
}

export function outcomeOf(a: DataAnswer | null | undefined): Outcome | null {
  if (!a) return null;
  const o = a.outcome;
  return o === 'answered' || o === 'built' || o === 'needs-you' || o === 'refused' || o === 'error' ? o : 'error';
}

/** The parts to show after an answer. A refusal can come back with no parts at all; the chips the person sent
 *  stay, because wiping them would throw away the question they were fixing. */
export function partsAfter(sent: DataAsk, answer: DataAnswer | null | undefined): DataAsk {
  return answer?.parts ? cleanParts(answer.parts) : cleanParts(sent);
}

// ---- names -----------------------------------------------------------------------------------------------------

/** 'Store'[Store Region] or Store[Store Region] to "Store Region". */
export function columnName(ref: string): string {
  const m = /\[((?:[^\]]|\]\])+)\]\s*$/.exec(ref ?? '');
  return m ? m[1].replace(/\]\]/g, ']') : ref;
}
export function tableName(ref: string): string {
  const m = /^\s*'((?:[^']|'')+)'/.exec(ref ?? '') ?? /^\s*([^[']+)\[/.exec(ref ?? '');
  return m ? m[1].replace(/''/g, "'").trim() : '';
}
export function columnRef(table: string, name: string): string {
  return `'${table.replace(/'/g, "''")}'[${name.replace(/]/g, ']]')}]`;
}

const OP_WORDS: Record<string, string> = { is: '=', is_not: 'is not', after: 'after', from: 'from', before: 'before', up_to: 'up to' };
export function filterWords(f: DataAskFilter): string {
  return `${columnName(f.column)} ${filterValueWords(f)}`;
}
/** A filter part without its column: "2025", "is not North", "after 2023". */
export function filterValueWords(f: DataAskFilter): string {
  const op = OP_WORDS[f.op] ?? '=';
  return `${op === '=' ? '= ' : op + ' '}${(f.values ?? []).join(' or ')}`;
}

/** The question in one line, from its parts: "Net Sales by Region, Region = Atlantis, top 10 by Net Sales". The
 *  history row's name when the engine had no reading to give, which is every question refused before its parts
 *  were settled (a value not in the data, no model with data, the assistant's ask held for approval). */
export function questionLine(p: DataAsk | null | undefined): string {
  if (!p || !(p.measures ?? []).length) return '';
  const chips = chipsOf(p);
  const groups = chips.filter((c) => c.kind === 'group').map((c) => c.label);
  const rest = chips.filter((c) => c.kind !== 'measure' && c.kind !== 'group').map((c) => c.label);
  return [(p.measures ?? []).join(' and ') + (groups.length ? ` by ${groups.join(' and ')}` : ''), ...rest].join(', ');
}

export type ChipKind = 'measure' | 'group' | 'filter' | 'top' | 'sort';
export interface Chip { kind: ChipKind; index: number; label: string; name: string }
/** The question as chips, in reading order. `name` is what a screen reader says for the button. */
export function chipsOf(p: DataAsk): Chip[] {
  const out: Chip[] = [];
  (p.measures ?? []).forEach((m, i) => out.push({ kind: 'measure', index: i, label: m, name: `Change the number ${m}` }));
  (p.groupBy ?? []).forEach((g, i) => out.push({ kind: 'group', index: i, label: columnName(g), name: `Change the split by ${columnName(g)}` }));
  (p.filters ?? []).forEach((f, i) => out.push({ kind: 'filter', index: i, label: filterWords(f), name: `Change the filter ${filterWords(f)}` }));
  if (p.top) {
    const label = `${p.lowest ? 'Bottom' : 'Top'} ${p.top} by ${p.topBy ?? (p.measures ?? [])[0] ?? ''}`.trim();
    out.push({ kind: 'top', index: 0, label, name: `Change ${label}` });
  } else if (p.sortBy) {
    const by = /\[/.test(p.sortBy) ? columnName(p.sortBy) : p.sortBy;
    const label = `Sorted by ${by}, ${p.descending ? 'largest first' : 'smallest first'}`;
    out.push({ kind: 'sort', index: 0, label, name: `Change the order, ${label}` });
  }
  return out;
}

// ---- edits -----------------------------------------------------------------------------------------------------

const set = <T,>(list: T[], i: number, v: T) => list.map((x, j) => (j === i ? v : x));
const drop = <T,>(list: T[], i: number) => list.filter((_, j) => j !== i);

export function swapMeasure(p: DataAsk, i: number, name: string): DataAsk {
  const c = cleanParts(p);
  const old = c.measures![i];
  if (c.measures!.includes(name)) return c;
  c.measures = set(c.measures!, i, name);
  if (c.topBy === old) c.topBy = name;
  if (c.sortBy === old) c.sortBy = name;
  return c;
}
export function addMeasure(p: DataAsk, name: string): DataAsk {
  const c = cleanParts(p);
  if (!c.measures!.includes(name)) c.measures = [...c.measures!, name];
  return c;
}
/** A question needs one number, so the last one stays. */
export function removeMeasure(p: DataAsk, i: number): DataAsk {
  const c = cleanParts(p);
  if (c.measures!.length <= 1) return c;
  const gone = c.measures![i];
  c.measures = drop(c.measures!, i);
  if (c.topBy === gone) c.topBy = c.measures[0];
  if (c.sortBy === gone) { c.sortBy = null; c.descending = false; }
  return c;
}
export function swapGroup(p: DataAsk, i: number, ref: string): DataAsk {
  const c = cleanParts(p);
  if (c.groupBy!.includes(ref)) return c;
  const old = c.groupBy![i];
  c.groupBy = set(c.groupBy!, i, ref);
  if (c.sortBy === old) c.sortBy = ref;
  return c;
}
export function addGroup(p: DataAsk, ref: string): DataAsk {
  const c = cleanParts(p);
  if (!c.groupBy!.includes(ref)) c.groupBy = [...c.groupBy!, ref];
  return c;
}
export function removeGroup(p: DataAsk, i: number): DataAsk {
  const c = cleanParts(p);
  const gone = c.groupBy![i];
  c.groupBy = drop(c.groupBy!, i);
  if (c.sortBy === gone) { c.sortBy = null; c.descending = false; }
  return c;
}
/** Typed words, split on commas. The engine matches them against the column's values, or refuses in plain words. */
export function wordsOf(text: string): string[] {
  return text.split(',').map((w) => w.trim()).filter(Boolean);
}
export function addFilter(p: DataAsk, column: string, words: string, op = 'is'): DataAsk {
  const c = cleanParts(p);
  c.filters = [...c.filters!, { column, op, values: wordsOf(words) }];
  return c;
}
export function setFilterValues(p: DataAsk, i: number, words: string, op?: string): DataAsk {
  const c = cleanParts(p);
  c.filters = set(c.filters!, i, { ...c.filters![i], op: op ?? c.filters![i].op, values: wordsOf(words) });
  return c;
}
export function removeFilter(p: DataAsk, i: number): DataAsk {
  const c = cleanParts(p);
  c.filters = drop(c.filters!, i);
  return c;
}
export function setTop(p: DataAsk, n: number, by?: string, lowest = false): DataAsk {
  const c = cleanParts(p);
  c.top = n; c.topBy = by ?? c.topBy ?? c.measures![0] ?? null; c.lowest = lowest;
  c.sortBy = null; c.descending = false;
  return c;
}
export function removeTop(p: DataAsk): DataAsk {
  const c = cleanParts(p);
  c.top = null; c.topBy = null; c.lowest = false;
  return c;
}
export function setSort(p: DataAsk, by: string, descending: boolean): DataAsk {
  const c = cleanParts(p);
  c.sortBy = by; c.descending = descending; c.top = null; c.topBy = null; c.lowest = false;
  return c;
}
export function removeSort(p: DataAsk): DataAsk {
  const c = cleanParts(p);
  c.sortBy = null; c.descending = false;
  return c;
}

// ---- pickers ---------------------------------------------------------------------------------------------------

/** The columns a question may split or filter by: every column outside a calculation group. A calculation group's
 *  column applies calculation items rather than naming things, so the engine refuses it every time and leaves it
 *  out of its own catalogue (QueryCatalog.Read); offering it here was a chip it will always refuse. */
export function askableColumns<T extends { table: string }>(rows: T[], calculationGroups: Iterable<string>): T[] {
  const calc = new Set(calculationGroups);
  return rows.filter((r) => !calc.has(r.table));
}

export interface ColumnInfo { table: string; name: string; dataType?: string }
/** The columns a filter may use: text, numbers and dates, the kinds the engine filters (QueryTemplate.Filterable). A
 *  column of days takes a range only (after, from, before or up to one day), the same range the assistant can send.
 *  True or false is not offered: the engine cannot filter it yet, and a chip it will always refuse is a trap. */
export function filterableColumns<T extends ColumnInfo>(rows: T[]): T[] {
  return rows.filter((r) => !/bool/i.test(r.dataType ?? ''));
}

/** How a filter on this column is written: a list of values, a number that can be bounded, or one day that bounds a
 *  range. The engine refuses "is" on a column of days, so a date chip offers only the four range words. */
export function filterKind(dataType?: string | null): 'text' | 'number' | 'date' {
  if (/date|time/i.test(dataType ?? '')) return 'date';
  return /int|decimal|double|currency|number/i.test(dataType ?? '') ? 'number' : 'text';
}

/** The operator to show for a column of this kind: the current one when the kind takes it, else the first it takes.
 *  A range word left over from a date column is never sent for a text column (Sol, 29 Sep). */
export function opFor(kind: 'text' | 'number' | 'date', current: string): string {
  const ops = filterOps(kind);
  return ops.some(([v]) => v === current) ? current : kind === 'date' ? 'from' : ops[0][0];
}

/** The operators a filter of this kind may use, as [op, words]. */
export function filterOps(kind: 'text' | 'number' | 'date'): [string, string][] {
  const range: [string, string][] = [['after', 'after'], ['from', 'from'], ['before', 'before'], ['up_to', 'up to']];
  return kind === 'date' ? range : kind === 'number' ? [['is', 'is'], ['is_not', 'is not'], ...range] : [['is', 'is'], ['is_not', 'is not']];
}

// ---- the result --------------------------------------------------------------------------------------------------

/** The engine's line when a question needs a model that holds data (LocalEngine.Query.cs NoDataNote, word for word;
 *  test/ask-data-page.test.mjs holds the two together). It arrives as a note on a built answer, as the unsure line of
 *  a question in words, or inside the refused parts check of a question with a filter. */
export const NO_DATA_LINE = 'Connect a copy of this model that holds data. The words are matched against its values, and the query runs on it.';

/** Whether this answer is waiting on a model with data, so the page offers the way to connect. Every outcome that
 *  says so, not only "built": a filter's values are matched on the data, so without it the question is refused. */
export function needsData(a: DataAnswer | null | undefined): boolean {
  if (!a) return false;
  if (a.outcome === 'built') return true;
  const lines = [a.note ?? '', ...(a.unsure ?? []), ...(a.checks ?? []).filter((c) => !c.passed).map((c) => c.line)];
  return lines.some((l) => l.includes(NO_DATA_LINE));
}

export function resultSetOf(a: DataAnswer): ResultSet {
  // The answer's formats ride on the columns, so every place that draws this result (the grid, a matrix, a card, a chart's
  // axis and tooltip) reads the same format and Return Rate % 0.0068 is 0.7% in all of them, never a rounded 0.01 (Sol 3.1).
  const columns = (a.columns ?? []).map((c, i) => (a.formats?.[i] ? { ...c, format: a.formats[i]! } : c));
  return { columns, rows: a.rows ?? [], rowCount: a.rowCount ?? (a.rows ?? []).length, truncated: !!a.truncated, elapsedMs: a.elapsedMs ?? 0 };
}

/** The shared chart reads a PivotConfig. The parts map straight onto one: the splits are the rows, the numbers
 *  are the values. The refs are the answer's own column names, because the chart reads the result, not the model. */
export function pivotConfigOf(p: DataAsk, dax?: string | null): PivotConfig {
  const rows: WellField[] = (p.groupBy ?? []).map((g) => ({ kind: 'column', ref: g, name: columnName(g), table: tableName(g) }));
  const values: WellField[] = (p.measures ?? []).map((m) => ({ kind: 'measure', ref: `[${m}]`, name: m }));
  // The question's own filters ride along as the engine wrote them, so a number's context (its tooltip, Explain this
  // number, Compare a rewrite) is the context the number was computed in. The wells cannot hold these, and without
  // them Explain on "Coast in 2025" explained every year's Coast (verify-queries-redesign D1).
  const lines = answerFilterLines(dax);
  const filters = p.filters ?? [];
  const context = lines.map((line, i) => ({ name: filters[i] ? columnName(filters[i].column) : 'Filter', value: filters[i] ? filterValueWords(filters[i]) : line, dax: line }));
  return { rows, cols: [], values, filters: [], ...(context.length ? { context } : {}) };
}

/** The filter lines of an answer's query, word for word as the engine's template wrote them: one VAR __FilterN per
 *  filter part, in order (QueryTemplate.Build). Each is a table a CALCULATE or SUMMARIZECOLUMNS takes as a filter. */
export function answerFilterLines(dax: string | null | undefined): string[] {
  const define = /^DEFINE\n([\s\S]*?)    VAR __Result =/m.exec((dax ?? '').replace(/\r\n/g, '\n'));
  return define ? define[1].split(/^\s*VAR __Filter\d+ =\s*$/m).map((x) => x.trim()).filter(Boolean) : [];
}

export function whoAsked(r: { origin?: string | null }): string {
  return r.origin === 'agent' ? 'Your assistant' : 'You';
}

// ---- save as a check -------------------------------------------------------------------------------------------

const text = (s: string) => `"${s.replace(/"/g, '""')}"`;
/** A cell as a DAX literal, or null when the page cannot say it safely: a blank, a date, or true or false. */
function literal(v: unknown, type?: string): string | null {
  if (v == null) return null;
  if (/date|time|bool/i.test(type ?? '')) return null;
  if (typeof v === 'number' && Number.isFinite(v)) return String(v);
  if (typeof v === 'string') return text(v);
  return null;
}

export interface CheckDraft { id: ''; kind: 'measureValue'; title: string; targetRef: string; enabled: true; paramsJson: string }

/**
 * One number from the answer, as a measureValue check: the measure, the value shown, and one CALCULATE that asks
 * for exactly that number. It keeps the engine's own filter lines (the VARs its template wrote, inlined) and
 * narrows to the row with TREATAS over the row's own values. Null when the row cannot be pinned (a blank, or a
 * split by a date), or the measure is not in the model.
 */
export function checkDraftOf(a: DataAnswer, row: number, measure: number, measuresInModel: { name: string; table: string }[]): CheckDraft | null {
  const parts = a.parts ?? {};
  const groups = parts.groupBy ?? [];
  const measures = parts.measures ?? [];
  const name = measures[measure];
  const cells = (a.rows ?? [])[row];
  if (!name || !cells || !a.dax) return null;
  const found = measuresInModel.find((m) => m.name === name);
  if (!found) return null;
  const valueCell = cells[groups.length + measure];
  if (valueCell == null) return null;
  const narrow: string[] = [];
  for (let i = 0; i < groups.length; i++) {
    const lit = literal(cells[i], a.columns?.[i]?.type);
    if (lit == null) return null;
    narrow.push(`TREATAS ( { ${lit} }, ${groups[i]} )`);
  }
  // A trusted-answer check asks ONE value and refuses a DEFINE query (measured live, 26 Sep), so each of the
  // engine's filter VARs is inlined as a CALCULATE filter, word for word as the template wrote it.
  const filters = answerFilterLines(a.dax);
  const args = [`[${name.replace(/]/g, ']]')}]`, ...filters, ...narrow].join(', ');
  const expressionDax = `CALCULATE ( ${args} )`;
  const expected = String(valueCell);
  const measureRef = `measure:${found.table}/${found.name}`;
  return {
    id: '', kind: 'measureValue', enabled: true, targetRef: measureRef,
    title: checkTitle(measureRef, expected, { expressionDax }),
    paramsJson: JSON.stringify({ measureRef, expectedValue: expected, expressionDax, provenance: a.reading ? `Ask your data: ${a.reading}` : undefined }),
  };
}

/**
 * A saved check's title, read off the check itself: the measure, every condition it asks, and the number it expects.
 * "Net Sales for Year = 2025, Store Region = Coast is 5281775.96"; with no condition at all, "Net Sales in total is 9".
 *
 * WHY IT READS THE CHECK. Twice a title was carried along beside the check and kept while "nothing that matters"
 * changed, and twice a change it did not watch made it false: the New check drawer fell back to "<measure> totals
 * <number>" for a one-row check when the number was typed (verify-askdata-r2 R2), then when an Only where filter was
 * added or the row in the formula was edited (verify-askdata-r3 R2). A title worked out from the fields every time
 * is true whatever field changes. Only a check that asks nothing narrower than the measure is called a total.
 *
 * The conditions are read from the shapes the engine's query template writes (QueryTemplate.cs FilterExpression:
 * TREATAS for "is", NOT IN for "is not", one comparison for after, from, before and up to) and from the drawer's own
 * Only where and written filter fields. A formula that is anything other than CALCULATE over the measure with those
 * shapes is named as an edited formula rather than guessed at.
 */
export function checkTitle(measureRef: string, expected: string, fields: {
  expressionDax?: string; filterColumn?: string; filterValue?: string; filterDax?: string;
}): string {
  // A ref is measure:<table>/<name> and the name is everything after the FIRST slash, the way the engine splits it
  // (ObjectRefs.Split): a measure's name may hold a slash of its own ("Sales/Unit"), so the last piece is not the name.
  const ref = (measureRef || '').replace(/^measure:/, '');
  const name = (ref.includes('/') ? ref.slice(ref.indexOf('/') + 1) : ref) || 'Calculation';
  const x = (fields.expressionDax ?? '').trim();
  const read = x ? conditionsOf(x, name) : [];
  const where = [...(read ?? [])];
  // The written filter REPLACES Only where when both are set, because that is the one the engine asks
  // (LocalEngine.TestSuite.cs, the measure-value filter), so the title names that one and not both.
  const fc = (fields.filterColumn ?? '').trim(), fv = (fields.filterValue ?? '').trim();
  const fd = (fields.filterDax ?? '').trim();
  if (fd) where.push(...(topLevelArgs(fd) ?? [fd]).map((c) => conditionWords(c) ?? 'a written filter'));
  else if (fc && fv) where.push(`${columnName(fc)} = ${fv}`);
  const what = read === null ? `${name} with an edited formula` : where.length ? name : `${name} in total`;
  const n = (expected ?? '').trim();
  return `${what}${where.length ? ` for ${where.join(', ')}` : ''}${n ? ` is ${n}` : ''}`;
}

/** The conditions a CALCULATE over the measure asks, in words; [] for the bare measure; null for anything else. */
function conditionsOf(expression: string, measure: string): string[] | null {
  const bare = BRACKETED.exec(expression);
  if (bare) return bare[1].replace(/\]\]/g, ']') === measure ? [] : null;
  const call = /^CALCULATE\s*\(([\s\S]*)\)$/i.exec(expression);
  const args = call ? topLevelArgs(call[1]) : null;
  if (!args || !args.length) return null;
  const first = BRACKETED.exec(args[0]);
  if (!first || first[1].replace(/\]\]/g, ']') !== measure) return null;
  const words = args.slice(1).map(conditionWords);
  return words.every((w): w is string => w != null) ? words : null;
}

/** Split a DAX argument list at its top-level commas, stepping over strings, quoted names, brackets and braces.
 *  Null when the text does not balance. */
function topLevelArgs(s: string): string[] | null {
  const out: string[] = [];
  let depth = 0, start = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    const close = c === '"' ? '"' : c === "'" ? "'" : c === '[' ? ']' : null;
    if (close) {
      i++;
      // A doubled closer is an escaped one inside the name or the string ("", '', ]]).
      while (i < s.length && !(s[i] === close && s[i + 1] !== close)) i += s[i] === close ? 2 : 1;
      if (i >= s.length) return null;
      continue;
    }
    if (c === '(' || c === '{') depth++;
    else if (c === ')' || c === '}') { if (--depth < 0) return null; }
    else if (c === ',' && depth === 0) { out.push(s.slice(start, i).trim()); start = i + 1; }
  }
  if (depth !== 0) return null;
  const last = s.slice(start).trim();
  if (last || out.length) out.push(last);
  return out;
}

const BRACKETED = /^\[((?:[^\]]|\]\])+)\]$/;
const COL = String.raw`(?:'(?:[^']|'')+'|[A-Za-z_][\w ]*)?\[(?:[^\]]|\]\])+\]`;
const TREATAS_IS = new RegExp(String.raw`^TREATAS\s*\(\s*\{([\s\S]*)\}\s*,\s*(${COL})\s*\)$`, 'i');
const NOT_IN = new RegExp(String.raw`^FILTER\s*\(\s*KEEPFILTERS\s*\(\s*VALUES\s*\(\s*(${COL})\s*\)\s*\)\s*,\s*NOT\s*\(\s*(${COL})\s+IN\s*\{([\s\S]*)\}\s*\)\s*\)$`, 'i');
const COMPARED = new RegExp(String.raw`^FILTER\s*\(\s*KEEPFILTERS\s*\(\s*VALUES\s*\(\s*(${COL})\s*\)\s*\)\s*,\s*(${COL})\s*(>=|<=|>|<)\s*([\s\S]+?)\s*\)$`, 'i');
const PLAIN = new RegExp(String.raw`^(${COL})\s*(=|<>|>=|<=|>|<)\s*([\s\S]+)$`);
const SIGN_WORDS: Record<string, string> = { '=': '=', '<>': 'is not', '>': 'after', '>=': 'from', '<': 'before', '<=': 'up to' };

/** One filter argument in words ("Year = 2025", "Channel is not Online"), or null for a shape this does not read. */
function conditionWords(arg: string): string | null {
  const a = arg.trim();
  let m = TREATAS_IS.exec(a);
  if (m) { const v = valuesOf(m[1]); return v && `${columnName(m[2])} = ${v.join(' or ')}`; }
  m = NOT_IN.exec(a);
  if (m) { const v = valuesOf(m[3]); return v && `${columnName(m[2])} is not ${v.join(' or ')}`; }
  const compared = COMPARED.exec(a);
  const plain = compared ? null : PLAIN.exec(a);
  const [col, sign, value] = compared ? [compared[2], compared[3], compared[4]] : plain ? [plain[1], plain[2], plain[3]] : [];
  if (!col || !sign || !value) return null;
  const v = valuesOf(value);
  return v && v.length === 1 ? `${columnName(col)} ${SIGN_WORDS[sign]} ${v[0]}` : null;
}

/** DAX literals as the words a person reads: a string without its quotes, a number as written. */
function valuesOf(list: string): string[] | null {
  const items = topLevelArgs(list);
  if (!items || !items.length) return null;
  const out: string[] = [];
  for (const item of items) {
    const s = /^"((?:[^"]|"")*)"$/.exec(item);
    if (s) out.push(s[1].replace(/""/g, '"'));
    else if (/^-?\d+(\.\d+)?([eE][-+]?\d+)?$/.test(item)) out.push(item);
    else return null;
  }
  return out;
}

/** A row's label for the Save as a check chooser. */
export function rowLabel(a: DataAnswer, row: number): string {
  const n = (a.parts?.groupBy ?? []).length;
  const cells = (a.rows ?? [])[row] ?? [];
  return n === 0 ? 'The total' : cells.slice(0, n).map((c) => (c == null ? '(blank)' : String(c))).join(', ');
}

/** What a chip menu's finder says when its list is empty: the typed words matched nothing, or there was nothing to
 *  list. Said in the person's own words, so a typo reads differently from a column the model does not have. */
export function emptyFinderLine(what: 'number' | 'column', typed: string): string {
  const words = typed.trim();
  if (words) return `No ${what} matches "${words}". Check the spelling, or clear the words to see them all.`;
  return what === 'number' ? 'This model has no measures to show.' : 'There are no columns to show here.';
}
